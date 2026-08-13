using Mono.Cecil;
using NUnit.Framework;
using TestTrace.Core;

namespace TestTrace.Core.Tests;

[TestFixture]
public class TUnitTestDetectorTests
{
    private static ModuleDefinition _module = null!;
    private readonly TUnitTestDetector _detector = new();

    [OneTimeSetUp]
    public void BuildFixtureAssembly() => _module = FakeTUnitAssembly.Build();

    [OneTimeTearDown]
    public void Cleanup() => _module.Dispose();

    private static MethodDefinition Method(string type, string name) =>
        _module.GetType(type).Methods.First(m => m.Name == name);

    [Test]
    public void Test_IsATest_ButNotParameterized()
    {
        var test = Method("Tests.DeliveryTests", "PlainTest");

        Assert.Multiple(() =>
        {
            Assert.That(_detector.IsTestMethod(test), Is.True);
            Assert.That(_detector.IsParameterizedTest(test), Is.False);
        });
    }

    [Test]
    public void AttributeDerivedFromTest_IsStillATest() =>
        Assert.That(_detector.IsTestMethod(Method("Tests.DeliveryTests", "SlowTest")), Is.True);

    [Test]
    public void Hooks_AreLifecycleNotTests()
    {
        // TUnit has no constructor-as-setup convention like xUnit; [Before]/[After]
        // hooks are the lifecycle, and they must not be mistaken for tests.
        var setup = Method("Tests.DeliveryTests", "Setup");
        var teardown = Method("Tests.DeliveryTests", "Teardown");

        Assert.Multiple(() =>
        {
            Assert.That(_detector.GetLifecycleScope(setup), Is.EqualTo(TestLifecycleScope.Fixture));
            Assert.That(_detector.GetLifecycleScope(teardown), Is.EqualTo(TestLifecycleScope.Fixture));
            Assert.That(_detector.IsTestMethod(setup), Is.False);
        });
    }

    [Test]
    public void OrdinaryMethod_IsNeitherTestNorLifecycle()
    {
        var helper = Method("Tests.DeliveryTests", "Helper");

        Assert.Multiple(() =>
        {
            Assert.That(_detector.IsTestMethod(helper), Is.False);
            Assert.That(_detector.GetLifecycleScope(helper), Is.EqualTo(TestLifecycleScope.None));
        });
    }

    [Test]
    public void Arguments_MakeATestParameterized()
    {
        // Verified against `--list-tests`: these run once per row with the arguments in
        // the node name ("WithArguments(1, 2)"), so the tree path needs a wildcard.
        Assert.That(_detector.IsParameterizedTest(Method("Tests.DeliveryTests", "WithArguments")), Is.True);
    }

    [Test]
    public void MethodDataSource_ResolvesToTheDeclaringType()
    {
        var sources = _detector.GetTestCaseSources(Method("Tests.DeliveryTests", "FromMethodSource")).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(sources, Is.EqualTo(new[] { new TestCaseSourceRef("Tests.DeliveryTests", "LineCounts") }));
            Assert.That(_detector.IsParameterizedTest(Method("Tests.DeliveryTests", "FromMethodSource")), Is.True);
        });
    }

    [Test]
    public void MethodDataSource_WithProvidingType_ResolvesToThatType()
    {
        var sources = _detector.GetTestCaseSources(Method("Tests.DeliveryTests", "FromOtherTypeSource")).ToList();

        Assert.That(sources, Is.EqualTo(new[] { new TestCaseSourceRef("Tests.CaseData", "Shared") }));
    }

    [Test]
    public void GenericClassDataSource_TakesEveryMemberOfTheTypeArgument()
    {
        // [ClassDataSource<T>] carries the type as a GENERIC argument, not a
        // constructor argument, so the matcher has to compare open generic types.
        var sources = _detector.GetTestCaseSources(Method("Tests.DeliveryTests", "FromClassSource")).ToList();

        Assert.That(sources, Is.EqualTo(new[] { new TestCaseSourceRef("Tests.CaseData", "*") }));
    }

    [Test]
    public void OtherDetectors_DoNotClaimTUnitTests()
    {
        // Discovery takes the union across frameworks, so overlapping claims would
        // mis-attribute a test and emit the wrong filter dialect for it.
        var test = Method("Tests.DeliveryTests", "PlainTest");

        Assert.Multiple(() =>
        {
            Assert.That(new NUnitTestDetector().IsTestMethod(test), Is.False);
            Assert.That(new XunitTestDetector().IsTestMethod(test), Is.False);
        });
    }

    [Test]
    public void Registry_ResolvesTUnitToTheTreeNodeDialect()
    {
        var tunit = TestFrameworks.ByName("tunit")!;

        Assert.Multiple(() =>
        {
            Assert.That(tunit, Is.TypeOf<TUnitTestDetector>());
            Assert.That(tunit.Dialect, Is.EqualTo(TestFilterDialect.TreeNode));
            Assert.That(tunit.MarkerAssemblies, Does.Contain("TUnit.Core"));
        });
    }

    [Test]
    public void Registry_KnowsEveryFrameworkByName()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TestFrameworks.Names, Is.EquivalentTo(new[] { "nunit", "xunit", "tunit", "mstest" }));
            Assert.That(TestFrameworks.ByName("TUNIT"), Is.TypeOf<TUnitTestDetector>(), "names are case-insensitive");
            Assert.That(TestFrameworks.ByName("junit"), Is.Null, "an unsupported framework resolves to nothing");
        });
    }
}
