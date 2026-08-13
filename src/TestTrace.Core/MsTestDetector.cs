using Mono.Cecil;

namespace TestTrace.Core;

/// <summary>
/// MSTest (v3). Attribute names and shapes were read off MSTest 3.11, not guessed.
///
/// MSTest is dual-runner: with EnableMSTestRunner it builds a Microsoft.Testing.Platform
/// executable like TUnit, otherwise it runs under VSTest. That distinction does NOT
/// reach testtrace, because its MTP mode takes <c>--filter</c> in **vstest syntax** and
/// rejects <c>--treenode-filter</c>, and an MTP-mode assembly is still drivable by
/// <c>dotnet vstest</c> through the bundled VSTestBridge. One expression language
/// either way, so the dialect is simply VsTest.
///
/// Two rules differ from the other detectors in ways that would be wrong if copied by
/// analogy — see IsParameterizedTest and IsSetupMethod.
/// </summary>
public sealed class MsTestDetector : ITestFrameworkDetector
{
    public string Name => "mstest";

    public TestFilterDialect Dialect => TestFilterDialect.VsTest;

    /// <summary>Referenced in both VSTest and MTP mode, so one marker covers both.</summary>
    public IReadOnlyList<string> MarkerAssemblies { get; } = ["Microsoft.VisualStudio.TestPlatform.TestFramework"];

    private const string Ns = "Microsoft.VisualStudio.TestTools.UnitTesting.";

    /// <summary>[DataTestMethod] and [STATestMethod] both derive from [TestMethod], so
    /// the base covers the family — as does any project-specific derivative.</summary>
    private static readonly AttributeMatcher TestAttributes = new(Ns + "TestMethodAttribute");

    /// <summary>
    /// Unlike NUnit's and TUnit's, these share no common base, so they are enumerated.
    /// Missing one would mean a lifecycle change selected nothing.
    /// </summary>
    private static readonly AttributeMatcher FixtureLifecycleAttributes = new(
        Ns + "TestInitializeAttribute",
        Ns + "TestCleanupAttribute",
        Ns + "ClassInitializeAttribute",
        Ns + "ClassCleanupAttribute");

    /// <summary>Runs once around every test in the assembly, not just the declaring
    /// class's — the case that used to be mis-scoped to the fixture.</summary>
    private static readonly AttributeMatcher AssemblyLifecycleAttributes = new(
        Ns + "AssemblyInitializeAttribute",
        Ns + "AssemblyCleanupAttribute");

    /// <summary>MSTest 3.x global hooks: they run for every test, everywhere.</summary>
    private static readonly AttributeMatcher GlobalLifecycleAttributes = new(
        Ns + "GlobalTestInitializeAttribute",
        Ns + "GlobalTestCleanupAttribute");

    private static readonly AttributeMatcher DynamicDataAttribute = new(Ns + "DynamicDataAttribute");

    public bool IsTestMethod(MethodDefinition method) => TestAttributes.AnyOn(method);

    /// <summary>
    /// Always false, and deliberately so — do not "fix" this to match xUnit.
    ///
    /// Verified by running real filters: MSTest keeps data-row arguments out of
    /// FullyQualifiedName and puts them only in the display name, so
    /// <c>FullyQualifiedName=Ns.Class.WithDataRow</c> exact-matches and runs ALL of its
    /// rows. Contains-matching would still work but would drag in every test whose name
    /// merely starts with the same text.
    /// </summary>
    public bool IsParameterizedTest(MethodDefinition method) => false;

    /// <summary>
    /// Note there is no IsStatic guard here, unlike the xUnit detector: MSTest's
    /// [ClassInitialize] and [AssemblyInitialize] are static, and excluding statics
    /// would silently drop them.
    /// </summary>
    public TestLifecycleScope GetLifecycleScope(MethodDefinition method)
    {
        if (GlobalLifecycleAttributes.AnyOn(method))
            return TestLifecycleScope.Global;
        if (AssemblyLifecycleAttributes.AnyOn(method))
            return TestLifecycleScope.Assembly;
        return FixtureLifecycleAttributes.AnyOn(method)
            ? TestLifecycleScope.Fixture
            : TestLifecycleScope.None;
    }

    /// <summary>MSTest constructs test classes itself; nothing is injected by the runner.</summary>
    public IEnumerable<TypeReference> GetExternallyConstructedFixtures(TypeDefinition type) => [];

    public IEnumerable<TestCaseSourceRef> GetTestCaseSources(MethodDefinition method)
    {
        if (!method.HasCustomAttributes)
            yield break;

        foreach (var attribute in method.CustomAttributes)
        {
            if (!DynamicDataAttribute.Matches(attribute))
                continue;

            TestCaseSourceRef? source = null;
            try
            {
                source = DynamicDataSource(attribute, method);
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

    /// <summary>
    /// [DynamicData("Name")] or [DynamicData("Name", typeof(Other))]. Across all six
    /// overloads argument 0 is the member name and a declaring type, when given, is
    /// argument 1 — the other second arguments are an enum and an object[], so a
    /// TypeReference there is unambiguous.
    /// </summary>
    private static TestCaseSourceRef? DynamicDataSource(CustomAttribute attribute, MethodDefinition method)
    {
        var arguments = attribute.ConstructorArguments;
        if (arguments.Count < 1 || arguments[0].Value is not string memberName)
            return null;

        var declaring = arguments.Count >= 2 && arguments[1].Value is TypeReference provider
            ? provider.FullName
            : method.DeclaringType.FullName;

        return new TestCaseSourceRef(declaring, memberName);
    }
}
