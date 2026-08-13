using System.Collections.Concurrent;
using Mono.Cecil;

namespace TestTrace.Core;

/// <summary>
/// xUnit.net (v2 and v3 — the attribute names are unchanged between them).
///
/// Two things differ structurally from NUnit and both matter for selection:
///
///   - There is no [SetUp]. xUnit builds a fresh instance of the test class for every
///     test, so the CONSTRUCTOR is the per-test setup, and IDisposable.Dispose /
///     IAsyncLifetime are the teardown. They are reported as lifecycle methods, which
///     makes a change in one select every test in the class.
///   - Shared state comes from IClassFixture&lt;T&gt;/ICollectionFixture&lt;T&gt;, whose
///     T is constructed by the runner through reflection. Nothing calls that
///     constructor statically, so it needs the explicit edge that
///     GetExternallyConstructedFixtures drives.
/// </summary>
public sealed class XunitTestDetector : ITestFrameworkDetector
{
    public string Name => "xunit";

    /// <summary>xUnit ships a VSTest adapter, so it shares NUnit's filter language.</summary>
    public TestFilterDialect Dialect => TestFilterDialect.VsTest;

    /// <summary>v2 splits the core across xunit.core; v3 renamed it.</summary>
    public IReadOnlyList<string> MarkerAssemblies { get; } = ["xunit.core", "xunit.v3.core"];

    private static readonly AttributeMatcher TestAttributes = new(
        "Xunit.FactAttribute",
        // TheoryAttribute derives from FactAttribute, so the base-chain walk would find
        // it anyway; naming it keeps the fast path fast.
        "Xunit.TheoryAttribute");

    private static readonly AttributeMatcher TheoryAttributes = new("Xunit.TheoryAttribute");

    private static readonly AttributeMatcher MemberDataAttribute = new("Xunit.MemberDataAttribute");

    private static readonly AttributeMatcher ClassDataAttribute = new("Xunit.ClassDataAttribute");

    private const string ClassFixtureInterface = "Xunit.IClassFixture`1";
    private const string CollectionFixtureInterface = "Xunit.ICollectionFixture`1";

    /// <summary>
    /// "Does this type contain xUnit tests?" — asked once per method by IsSetupMethod,
    /// so it is cached. Keyed by module plus type name because FullName alone is not
    /// unique across assemblies, and detectors are shared across the parallel scan.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _isTestClass = new(StringComparer.Ordinal);

    public bool IsTestMethod(MethodDefinition method) => TestAttributes.AnyOn(method);

    /// <summary>
    /// A [Theory] runs once per data row and its runtime name carries the arguments, so
    /// filters must contains-match. [Fact] does not.
    /// </summary>
    public bool IsParameterizedTest(MethodDefinition method) => TheoryAttributes.AnyOn(method);

    /// <summary>
    /// The constructor, Dispose and the IAsyncLifetime pair, on a class that holds
    /// tests. xUnit re-instantiates the class per test, so all of these run around
    /// every test in it and a change to one impacts all of them.
    /// </summary>
    public TestLifecycleScope GetLifecycleScope(MethodDefinition method)
    {
        if (method.IsStatic)
            return TestLifecycleScope.None;

        var isLifecycleShape = method.IsConstructor
                               || method.Name is "Dispose" or "DisposeAsync" or "InitializeAsync";

        // Always Fixture: xUnit v2 has no assembly-level lifecycle. (v3's
        // IAssemblyFixture would map to Assembly if support is added.)
        return isLifecycleShape && IsTestClass(method.DeclaringType)
            ? TestLifecycleScope.Fixture
            : TestLifecycleScope.None;
    }

    public IEnumerable<TestCaseSourceRef> GetTestCaseSources(MethodDefinition method)
    {
        if (!method.HasCustomAttributes)
            yield break;

        foreach (var attribute in method.CustomAttributes)
        {
            TestCaseSourceRef? source = null;
            try
            {
                if (MemberDataAttribute.Matches(attribute))
                    source = MemberDataSource(attribute, method);
                else if (ClassDataAttribute.Matches(attribute))
                    source = ClassDataSource(attribute);
            }
            catch (Exception)
            {
                // Unresolvable attribute arguments: skip. A provider that is called
                // statically anywhere is still reached by the ordinary walk.
            }

            if (source is not null)
                yield return source.Value;
        }
    }

    /// <summary>[MemberData("Name")], or [MemberData("Name", MemberType = typeof(Other))].</summary>
    private static TestCaseSourceRef? MemberDataSource(CustomAttribute attribute, MethodDefinition method)
    {
        if (attribute.ConstructorArguments.Count < 1 || attribute.ConstructorArguments[0].Value is not string memberName)
            return null;

        var declaring = attribute.Properties.Concat(attribute.Fields)
            .Where(p => p.Name == "MemberType")
            .Select(p => p.Argument.Value as TypeReference)
            .FirstOrDefault(t => t is not null);

        return new TestCaseSourceRef(declaring?.FullName ?? method.DeclaringType.FullName, memberName);
    }

    /// <summary>[ClassData(typeof(T))] — the whole type is the data source, so every
    /// member of it counts ("*" is the wildcard the graph builder understands).</summary>
    private static TestCaseSourceRef? ClassDataSource(CustomAttribute attribute) =>
        attribute.ConstructorArguments.Count >= 1 && attribute.ConstructorArguments[0].Value is TypeReference dataType
            ? new TestCaseSourceRef(dataType.FullName, "*")
            : null;

    public IEnumerable<TypeReference> GetExternallyConstructedFixtures(TypeDefinition type)
    {
        foreach (var implementation in type.Interfaces)
        {
            if (implementation.InterfaceType is not GenericInstanceType generic ||
                generic.GenericArguments.Count != 1)
                continue;

            var name = generic.ElementType.FullName;
            if (name is ClassFixtureInterface or CollectionFixtureInterface)
                yield return generic.GenericArguments[0];
        }
    }

    private bool IsTestClass(TypeDefinition type)
    {
        var key = (type.Module?.Name ?? "") + "!" + type.FullName;
        return _isTestClass.GetOrAdd(key, _ => HasTestAnywhereInHierarchy(type));
    }

    /// <summary>Tests can be declared on a base class, so a derived class with no
    /// [Fact] of its own is still a test class and its constructor is still setup.</summary>
    private static bool HasTestAnywhereInHierarchy(TypeDefinition type)
    {
        var current = type;
        for (var depth = 0; current is not null && depth < 16; depth++)
        {
            if (current.Methods.Any(TestAttributes.AnyOn))
                return true;
            try
            {
                current = current.BaseType?.Resolve();
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }
}
