using Mono.Cecil;
using NUnit.Framework;
using TestTrace.Core;

namespace TestTrace.Core.Tests;

[TestFixture]
public class MsTestDetectorTests
{
    private static ModuleDefinition _module = null!;
    private readonly MsTestDetector _detector = new();

    [OneTimeSetUp]
    public void BuildFixtureAssembly() => _module = FakeMsTestAssembly.Build();

    [OneTimeTearDown]
    public void Cleanup() => _module.Dispose();

    private static MethodDefinition Method(string type, string name) =>
        _module.GetType(type).Methods.First(m => m.Name == name);

    [Test]
    public void TestMethod_IsATest() =>
        Assert.That(_detector.IsTestMethod(Method("Tests.OrderTotalTests", "PlainTest")), Is.True);

    [Test]
    public void DataTestMethod_DerivesFromTestMethod_AndIsStillATest() =>
        Assert.That(_detector.IsTestMethod(Method("Tests.OrderTotalTests", "DerivedTest")), Is.True);

    [Test]
    public void DataRowTest_IsNotTreatedAsParameterized()
    {
        // The one that must not be copied from xUnit or TUnit. Verified by running real
        // filters against the sample: MSTest keeps data-row arguments OUT of
        // FullyQualifiedName, so "FullyQualifiedName=...WithDataRow" exact-matches and
        // runs all of its rows. Contains-matching would drag in every test whose name
        // merely starts with the same text.
        var rows = Method("Tests.OrderTotalTests", "WithDataRow");

        Assert.Multiple(() =>
        {
            Assert.That(_detector.IsTestMethod(rows), Is.True);
            Assert.That(_detector.IsParameterizedTest(rows), Is.False);
        });
    }

    [Test]
    public void DynamicDataTest_IsAlsoNotParameterized() =>
        Assert.That(_detector.IsParameterizedTest(Method("Tests.OrderTotalTests", "FromOwnDynamicData")), Is.False);

    [Test]
    public void InstanceLifecycle_IsDetected()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_detector.GetLifecycleScope(Method("Tests.OrderTotalTests", "Setup")),
                Is.EqualTo(TestLifecycleScope.Fixture));
            Assert.That(_detector.GetLifecycleScope(Method("Tests.OrderTotalTests", "Teardown")),
                Is.EqualTo(TestLifecycleScope.Fixture));
        });
    }

    [Test]
    public void StaticLifecycle_IsDetected()
    {
        // The other trap: [ClassInitialize] and [AssemblyInitialize] are static. The
        // xUnit detector returns early on statics because its lifecycle is
        // instance-only; doing that here would silently drop these.
        var classInit = Method("Tests.OrderTotalTests", "ClassInit");
        var assemblyInit = Method("Tests.OrderTotalTests", "AssemblyInit");

        Assert.Multiple(() =>
        {
            Assert.That(classInit.IsStatic, Is.True, "fixture should model these as static");
            // [ClassInitialize] is the class's; [AssemblyInitialize] runs for the whole
            // assembly, and scoping it to the fixture used to lose every other class.
            Assert.That(_detector.GetLifecycleScope(classInit), Is.EqualTo(TestLifecycleScope.Fixture));
            Assert.That(_detector.GetLifecycleScope(assemblyInit), Is.EqualTo(TestLifecycleScope.Assembly));
        });
    }

    [Test]
    public void OrdinaryMethod_IsNeitherTestNorLifecycle()
    {
        var helper = Method("Tests.OrderTotalTests", "Helper");

        Assert.Multiple(() =>
        {
            Assert.That(_detector.IsTestMethod(helper), Is.False);
            Assert.That(_detector.GetLifecycleScope(helper), Is.EqualTo(TestLifecycleScope.None));
        });
    }

    [Test]
    public void DynamicData_ResolvesToTheDeclaringType()
    {
        var sources = _detector.GetTestCaseSources(Method("Tests.OrderTotalTests", "FromOwnDynamicData")).ToList();

        Assert.That(sources, Is.EqualTo(new[] { new TestCaseSourceRef("Tests.OrderTotalTests", "LineCounts") }));
    }

    [Test]
    public void DynamicData_WithDeclaringType_ResolvesToThatType()
    {
        // Argument 1 is a Type only in the overloads that name one; the others pass an
        // enum or an object[], so a TypeReference there is unambiguous.
        var sources = _detector.GetTestCaseSources(Method("Tests.OrderTotalTests", "FromOtherTypeDynamicData")).ToList();

        Assert.That(sources, Is.EqualTo(new[] { new TestCaseSourceRef("Tests.CaseData", "Rows") }));
    }

    [Test]
    public void DataRow_ContributesNoDataSource() =>
        Assert.That(_detector.GetTestCaseSources(Method("Tests.OrderTotalTests", "WithDataRow")), Is.Empty);

    [Test]
    public void OtherDetectors_DoNotClaimMsTestTests()
    {
        var test = Method("Tests.OrderTotalTests", "PlainTest");

        Assert.Multiple(() =>
        {
            Assert.That(new NUnitTestDetector().IsTestMethod(test), Is.False);
            Assert.That(new XunitTestDetector().IsTestMethod(test), Is.False);
            Assert.That(new TUnitTestDetector().IsTestMethod(test), Is.False);
        });
    }

    [Test]
    public void Registry_ResolvesMsTestToTheVsTestDialect()
    {
        var mstest = TestFrameworks.ByName("mstest")!;

        Assert.Multiple(() =>
        {
            Assert.That(mstest, Is.TypeOf<MsTestDetector>());
            // Dual-runner, one expression language: MSTest's Microsoft.Testing.Platform
            // mode takes --filter in vstest syntax and rejects --treenode-filter.
            Assert.That(mstest.Dialect, Is.EqualTo(TestFilterDialect.VsTest));
            Assert.That(mstest.MarkerAssemblies,
                Does.Contain("Microsoft.VisualStudio.TestPlatform.TestFramework"));
            Assert.That(TestFrameworks.Names, Does.Contain("mstest"));
        });
    }
}
