using System.Collections.Concurrent;
using Mono.Cecil;

namespace TestTrace.Core;

public readonly record struct TestCaseSourceRef(string TypeFullName, string MemberName);

/// <summary>
/// The filter language a framework's runner speaks. Selection is framework-agnostic —
/// the graph finds tests of every framework in one pass — but the emitted filter is
/// not portable, so it is chosen explicitly per run.
/// </summary>
public enum TestFilterDialect
{
    /// <summary>VSTest expressions: <c>FullyQualifiedName=A.B.C|FullyQualifiedName~A.B.D</c>.
    /// Used by <c>dotnet test --filter</c> and <c>dotnet vstest --TestCaseFilter</c>.</summary>
    VsTest,

    /// <summary>Microsoft.Testing.Platform tree paths:
    /// <c>/Assembly/*/(Class)/(Test)</c>, passed as <c>--treenode-filter</c>.</summary>
    TreeNode,
}

/// <summary>
/// How widely a lifecycle method reaches. A change inside one impacts every test in
/// its scope, so getting this wrong under-selects — which is why it is a scope rather
/// than the boolean it replaced: treating an assembly-wide hook as fixture-scoped
/// silently limited it to the tests of whichever class happened to declare it.
/// </summary>
public enum TestLifecycleScope
{
    /// <summary>Not a lifecycle method.</summary>
    None,

    /// <summary>Runs around the tests of its declaring type and types deriving from it
    /// — [SetUp], [TestInitialize], an xUnit constructor, [Before(Class)].</summary>
    Fixture,

    /// <summary>Runs around every test in its assembly — [AssemblyInitialize],
    /// [Before(Assembly)], and NUnit's [SetUpFixture] one-time pair.</summary>
    Assembly,

    /// <summary>Runs around every test in the run, across assemblies —
    /// [GlobalTestInitialize], TUnit's TestSession hooks and [BeforeEvery].</summary>
    Global,
}

/// <summary>
/// Test-framework specifics live behind this. NUnit and xUnit are both implemented;
/// MSTest can slot in the same way. Detectors are used concurrently across assemblies,
/// so implementations must be thread-safe.
/// </summary>
public interface ITestFrameworkDetector
{
    /// <summary>Lower-case identifier, as accepted by <c>--test-framework</c>.</summary>
    string Name { get; }

    /// <summary>Filter language this framework's runner accepts.</summary>
    TestFilterDialect Dialect { get; }

    /// <summary>
    /// Assembly names whose presence in a build means "this framework is here" —
    /// checked against each in-scope assembly's references. This is how the analyzer
    /// tells you the chosen framework is missing, or that another one is also present,
    /// without running every detector over every method to find out.
    /// </summary>
    IReadOnlyList<string> MarkerAssemblies { get; }

    bool IsTestMethod(MethodDefinition method);

    /// <summary>
    /// How far a lifecycle method reaches, or None when it is not one. A change here,
    /// or reaching one in the walk, impacts every test within that scope.
    /// </summary>
    TestLifecycleScope GetLifecycleScope(MethodDefinition method);

    /// <summary>Data source members referenced by [TestCaseSource]-style attributes.</summary>
    IEnumerable<TestCaseSourceRef> GetTestCaseSources(MethodDefinition method);

    /// <summary>True when the test's runtime FQN carries an argument list.</summary>
    bool IsParameterizedTest(MethodDefinition method);

    /// <summary>
    /// Types the framework constructs on this test class's behalf, by reflection —
    /// xUnit's IClassFixture&lt;T&gt;/ICollectionFixture&lt;T&gt;. Nothing calls their
    /// constructors statically, so without an explicit edge a change to one is
    /// invisible. Empty for frameworks without the concept.
    /// </summary>
    IEnumerable<TypeReference> GetExternallyConstructedFixtures(TypeDefinition type);
}

/// <summary>
/// Matches custom attributes by full name, and by inheritance: deriving from
/// FactAttribute to build a project-specific [IntegrationFact] is idiomatic xUnit, and
/// an exact-name-only match would classify every such test as "not a test" — which
/// reads downstream as "not affected", the silent miss the design forbids.
///
/// The exact-name check is the fast path. Base-chain resolution happens once per
/// attribute type and is then cached, so the cost is bounded by the number of distinct
/// attributes in the build rather than the number of methods.
/// </summary>
public sealed class AttributeMatcher
{
    private readonly HashSet<string> _names;
    private readonly ConcurrentDictionary<string, bool> _derived = new(StringComparer.Ordinal);

    public AttributeMatcher(params string[] names) => _names = new HashSet<string>(names, StringComparer.Ordinal);

    public bool Matches(CustomAttribute attribute)
    {
        // Generic attributes ([ClassDataSource<T>] in TUnit) have a FullName carrying
        // the argument list, so match the open type: "TUnit.Core.ClassDataSourceAttribute`1".
        var attributeType = attribute.AttributeType is GenericInstanceType generic
            ? generic.ElementType
            : attribute.AttributeType;

        var name = attributeType.FullName;
        if (_names.Contains(name))
            return true;

        // Keyed on the attribute's full name: two assemblies could in principle define
        // same-named attributes with different bases, in which case one entry serves
        // both. That can only over-select, which is the safe direction.
        return _derived.GetOrAdd(name, _ => DerivesFromMatch(attributeType));
    }

    public bool AnyOn(MethodDefinition method) =>
        method.HasCustomAttributes && method.CustomAttributes.Any(Matches);

    private bool DerivesFromMatch(TypeReference attributeType)
    {
        var current = TryResolve(attributeType)?.BaseType;
        for (var depth = 0; current is not null && depth < 8; depth++)
        {
            // Bases can be closed generics (TUnit's ArgumentsAttribute`1 derives from
            // TypedDataSourceAttribute`1<T>); compare against the open type.
            var element = current is GenericInstanceType generic ? generic.ElementType : current;
            if (_names.Contains(element.FullName))
                return true;
            current = TryResolve(element)?.BaseType;
        }

        return false;
    }

    private static TypeDefinition? TryResolve(TypeReference? reference)
    {
        try
        {
            return reference?.Resolve();
        }
        catch (Exception)
        {
            // Attribute defined in an assembly we cannot resolve: treat as unrelated.
            return null;
        }
    }
}

/// <summary>
/// The frameworks testtrace knows about.
///
/// A run targets exactly one framework, chosen through <c>--test-framework</c>: both
/// discovery and filter emission use that single detector. Nothing is inferred,
/// because the runners' filter languages are mutually unintelligible and guessing
/// wrong would emit a filter that runs the wrong tests rather than failing.
/// </summary>
public static class TestFrameworks
{
    public static IReadOnlyList<ITestFrameworkDetector> All { get; } =
        [new NUnitTestDetector(), new XunitTestDetector(), new TUnitTestDetector(), new MsTestDetector()];

    public static IReadOnlyList<string> Names { get; } = All.Select(d => d.Name).ToList();

    public static ITestFrameworkDetector? ByName(string? name) =>
        All.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
}

public sealed class NUnitTestDetector : ITestFrameworkDetector
{
    public string Name => "nunit";

    public TestFilterDialect Dialect => TestFilterDialect.VsTest;

    public IReadOnlyList<string> MarkerAssemblies { get; } = ["nunit.framework"];

    private static readonly AttributeMatcher TestAttributes = new(
        "NUnit.Framework.TestAttribute",
        "NUnit.Framework.TestCaseAttribute",
        "NUnit.Framework.TestCaseSourceAttribute",
        "NUnit.Framework.TheoryAttribute");

    private static readonly AttributeMatcher SetupAttributes = new(
        "NUnit.Framework.SetUpAttribute",
        "NUnit.Framework.OneTimeSetUpAttribute",
        "NUnit.Framework.TearDownAttribute",
        "NUnit.Framework.OneTimeTearDownAttribute");

    /// <summary>A [SetUpFixture] class holds one-time hooks for a whole NAMESPACE
    /// rather than for its own tests — it has none of its own.</summary>
    private static readonly AttributeMatcher SetUpFixtureAttribute = new("NUnit.Framework.SetUpFixtureAttribute");

    private static readonly AttributeMatcher ParameterizedAttributes = new(
        "NUnit.Framework.TestCaseAttribute",
        "NUnit.Framework.TestCaseSourceAttribute",
        "NUnit.Framework.TheoryAttribute");

    private static readonly AttributeMatcher TestCaseSourceAttribute = new(
        "NUnit.Framework.TestCaseSourceAttribute");

    public bool IsTestMethod(MethodDefinition method) => TestAttributes.AnyOn(method);

    /// <summary>
    /// The same [OneTimeSetUp] means different things depending on where it sits: in a
    /// [TestFixture] it is that fixture's setup, in a [SetUpFixture] it runs for every
    /// test in the namespace. The namespace case is widened to the whole assembly —
    /// an over-approximation, which is the safe direction, and far simpler than
    /// matching namespaces.
    /// </summary>
    public TestLifecycleScope GetLifecycleScope(MethodDefinition method)
    {
        if (!SetupAttributes.AnyOn(method))
            return TestLifecycleScope.None;

        var declaring = method.DeclaringType;
        return declaring.HasCustomAttributes && declaring.CustomAttributes.Any(SetUpFixtureAttribute.Matches)
            ? TestLifecycleScope.Assembly
            : TestLifecycleScope.Fixture;
    }

    public bool IsParameterizedTest(MethodDefinition method) => ParameterizedAttributes.AnyOn(method);

    /// <summary>NUnit constructs its fixtures itself; nothing is injected by the runner.</summary>
    public IEnumerable<TypeReference> GetExternallyConstructedFixtures(TypeDefinition type) => [];

    public IEnumerable<TestCaseSourceRef> GetTestCaseSources(MethodDefinition method)
    {
        if (!method.HasCustomAttributes)
            yield break;

        foreach (var attribute in method.CustomAttributes)
        {
            if (!TestCaseSourceAttribute.Matches(attribute))
                continue;

            TestCaseSourceRef? source = null;
            try
            {
                var args = attribute.ConstructorArguments;
                if (args.Count >= 1 && args[0].Value is string memberName)
                    source = new TestCaseSourceRef(method.DeclaringType.FullName, memberName);
                else if (args.Count >= 1 && args[0].Value is TypeReference sourceType)
                    source = new TestCaseSourceRef(
                        sourceType.FullName,
                        args.Count >= 2 && args[1].Value is string name ? name : "*");
            }
            catch (Exception)
            {
                // Unresolvable attribute arguments: skip; the provider is still covered
                // by the walk if it is called statically anywhere.
            }

            if (source is not null)
                yield return source.Value;
        }
    }
}
