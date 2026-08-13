using Mono.Cecil;

namespace TestTrace.Core;

/// <summary>
/// TUnit — a source-generated framework that runs on Microsoft.Testing.Platform
/// rather than VSTest. That platform difference is the reason filter emission is
/// framework-specific: TUnit takes <c>--treenode-filter "/Assembly/*/(Class)/(Test)"</c>
/// and has no notion of <c>FullyQualifiedName=</c>.
///
/// Discovery itself is ordinary attribute inspection. The attribute names below were
/// read off TUnit.Core 1.65, not guessed:
///
///   - Every test attribute derives from TUnit.Core.BaseTestAttribute, so matching the
///     base covers [Test] and [DynamicTestBuilder] alike.
///   - Every lifecycle hook derives from TUnit.Core.HookAttribute — [Before(Test)],
///     [After(Test)], [BeforeEvery], [AfterEvery] — so one match covers the family
///     regardless of which HookType the constructor was given.
///   - Data sources come in generic and non-generic forms ([ClassDataSource&lt;T&gt;]
///     versus [ClassDataSource(typeof(T))]), which AttributeMatcher handles by
///     comparing open types.
/// </summary>
public sealed class TUnitTestDetector : ITestFrameworkDetector
{
    public string Name => "tunit";

    public TestFilterDialect Dialect => TestFilterDialect.TreeNode;

    public IReadOnlyList<string> MarkerAssemblies { get; } = ["TUnit.Core"];

    private static readonly AttributeMatcher TestAttributes = new(
        "TUnit.Core.TestAttribute",
        // The base catches [DynamicTestBuilder] and any project-specific derivative.
        "TUnit.Core.BaseTestAttribute");

    private static readonly AttributeMatcher HookAttributes = new("TUnit.Core.HookAttribute");

    /// <summary>[BeforeEvery]/[AfterEvery] run around every test in the session, not
    /// just the declaring class's, whatever HookType they are given.</summary>
    private static readonly AttributeMatcher EveryHookAttributes = new(
        "TUnit.Core.BeforeEveryAttribute",
        "TUnit.Core.AfterEveryAttribute");

    /// <summary>TUnit.Core.HookType, read off the enum: Test=0, Class=1, Assembly=2,
    /// TestSession=3, TestDiscovery=4. It is the hook attribute's first argument.</summary>
    private const int HookTypeAssembly = 2;
    private const int HookTypeTestSession = 3;

    /// <summary>Anything that makes a test run more than once with different values, so
    /// its node name carries an argument list.</summary>
    private static readonly AttributeMatcher DataAttributes = new(
        "TUnit.Core.ArgumentsAttribute",
        "TUnit.Core.ArgumentsAttribute`1",
        "TUnit.Core.MethodDataSourceAttribute",
        "TUnit.Core.MethodDataSourceAttribute`1",
        "TUnit.Core.InstanceMethodDataSourceAttribute",
        "TUnit.Core.ClassDataSourceAttribute",
        "TUnit.Core.MatrixAttribute",
        "TUnit.Core.MatrixDataSourceAttribute",
        "TUnit.Core.CombinedDataSourcesAttribute",
        // Generic data-source generators: the arity-suffixed names are the open types.
        "TUnit.Core.DataSourceGeneratorAttribute`1",
        "TUnit.Core.AsyncDataSourceGeneratorAttribute`1",
        "TUnit.Core.UntypedDataSourceGeneratorAttribute",
        "TUnit.Core.AsyncUntypedDataSourceGeneratorAttribute");

    private static readonly AttributeMatcher MethodDataSourceAttributes = new(
        "TUnit.Core.MethodDataSourceAttribute",
        "TUnit.Core.MethodDataSourceAttribute`1",
        "TUnit.Core.InstanceMethodDataSourceAttribute");

    private static readonly AttributeMatcher ClassDataSourceAttributes = new(
        "TUnit.Core.ClassDataSourceAttribute",
        "TUnit.Core.ClassDataSourceAttribute`1",
        "TUnit.Core.ClassDataSourceAttribute`2",
        "TUnit.Core.ClassDataSourceAttribute`3",
        "TUnit.Core.ClassDataSourceAttribute`4",
        "TUnit.Core.ClassDataSourceAttribute`5");

    public bool IsTestMethod(MethodDefinition method) => TestAttributes.AnyOn(method);

    /// <summary>
    /// Verified against `--list-tests`: a TUnit test with data runs once per row and
    /// its node name carries the arguments ("WithArguments(1, 2)"), so the emitted
    /// tree path needs a trailing wildcard. A bare [Test] does not.
    /// </summary>
    public bool IsParameterizedTest(MethodDefinition method) => DataAttributes.AnyOn(method);

    /// <summary>
    /// [Before]/[After] hooks, scoped by the HookType they were given: Test and Class
    /// reach the declaring fixture, Assembly reaches the assembly, and TestSession /
    /// TestDiscovery reach everything. [BeforeEvery]/[AfterEvery] are global whatever
    /// their HookType.
    /// </summary>
    public TestLifecycleScope GetLifecycleScope(MethodDefinition method)
    {
        if (!method.HasCustomAttributes)
            return TestLifecycleScope.None;

        var widest = TestLifecycleScope.None;
        foreach (var attribute in method.CustomAttributes)
        {
            if (!HookAttributes.Matches(attribute))
                continue;

            var scope = EveryHookAttributes.Matches(attribute)
                ? TestLifecycleScope.Global
                : ScopeOf(attribute);
            if (scope > widest)
                widest = scope;
        }

        return widest;
    }

    /// <summary>Widest wins when a method carries several hooks; an unreadable
    /// HookType falls back to Assembly rather than Fixture, since guessing narrow
    /// would under-select.</summary>
    private static TestLifecycleScope ScopeOf(CustomAttribute attribute)
    {
        try
        {
            if (attribute.ConstructorArguments.Count == 0)
                return TestLifecycleScope.Fixture;

            return attribute.ConstructorArguments[0].Value switch
            {
                int hookType when hookType >= HookTypeTestSession => TestLifecycleScope.Global,
                int hookType when hookType == HookTypeAssembly => TestLifecycleScope.Assembly,
                int => TestLifecycleScope.Fixture,
                _ => TestLifecycleScope.Assembly,
            };
        }
        catch (Exception)
        {
            return TestLifecycleScope.Assembly;
        }
    }

    /// <summary>TUnit injects class-level data via [ClassDataSource], which is handled
    /// as a data source rather than as a runner-constructed fixture.</summary>
    public IEnumerable<TypeReference> GetExternallyConstructedFixtures(TypeDefinition type) => [];

    public IEnumerable<TestCaseSourceRef> GetTestCaseSources(MethodDefinition method)
    {
        if (!method.HasCustomAttributes)
            yield break;

        foreach (var attribute in method.CustomAttributes)
        {
            TestCaseSourceRef? source = null;
            try
            {
                if (MethodDataSourceAttributes.Matches(attribute))
                    source = MethodDataSource(attribute, method);
                else if (ClassDataSourceAttributes.Matches(attribute))
                    source = ClassDataSource(attribute);
            }
            catch (Exception)
            {
                // Unresolvable arguments: skip. A provider called statically anywhere
                // is still reached by the ordinary walk.
            }

            if (source is not null)
                yield return source.Value;
        }
    }

    /// <summary>[MethodDataSource("Name")] or [MethodDataSource(typeof(Other), "Name")].</summary>
    private static TestCaseSourceRef? MethodDataSource(CustomAttribute attribute, MethodDefinition method)
    {
        var arguments = attribute.ConstructorArguments;
        if (arguments.Count == 1 && arguments[0].Value is string ownName)
            return new TestCaseSourceRef(method.DeclaringType.FullName, ownName);

        if (arguments.Count >= 2 && arguments[0].Value is TypeReference provider &&
            arguments[1].Value is string memberName)
            return new TestCaseSourceRef(provider.FullName, memberName);

        return null;
    }

    /// <summary>
    /// [ClassDataSource(typeof(T))] carries the type as a constructor argument;
    /// [ClassDataSource&lt;T&gt;] carries it as a generic argument on the attribute
    /// itself. Either way every member of T counts, which is what "*" means to the
    /// graph builder.
    /// </summary>
    private static TestCaseSourceRef? ClassDataSource(CustomAttribute attribute)
    {
        if (attribute.AttributeType is GenericInstanceType generic && generic.GenericArguments.Count > 0)
            return new TestCaseSourceRef(generic.GenericArguments[0].FullName, "*");

        return attribute.ConstructorArguments.Count >= 1 &&
               attribute.ConstructorArguments[0].Value is TypeReference dataType
            ? new TestCaseSourceRef(dataType.FullName, "*")
            : null;
    }
}
