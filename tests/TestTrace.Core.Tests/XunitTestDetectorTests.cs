using Mono.Cecil;
using NUnit.Framework;
using TestTrace.Core;

namespace TestTrace.Core.Tests;

/// <summary>
/// Compiles a miniature xUnit-shaped assembly in memory rather than depending on the
/// sample solution: these assert the detector's rules, and the end-to-end proof that
/// selection works lives in the scenario suite.
/// </summary>
[TestFixture]
public class XunitTestDetectorTests
{
    private static ModuleDefinition _module = null!;
    private readonly XunitTestDetector _detector = new();

    [OneTimeSetUp]
    public void BuildFixtureAssembly() => _module = FakeXunitAssembly.Build();

    [OneTimeTearDown]
    public void Cleanup() => _module.Dispose();

    private static MethodDefinition Method(string type, string name) =>
        _module.GetType(type).Methods.First(m => m.Name == name);

    private static TypeDefinition Type(string name) => _module.GetType(name);

    [Test]
    public void Fact_IsATest_ButNotParameterized()
    {
        var fact = Method("Tests.PricingTests", "PlainFact");

        Assert.Multiple(() =>
        {
            Assert.That(_detector.IsTestMethod(fact), Is.True);
            Assert.That(_detector.IsParameterizedTest(fact), Is.False);
        });
    }

    [Test]
    public void Theory_IsATest_AndParameterized()
    {
        // Verified against `dotnet vstest --ListTests`: a theory's FullyQualifiedName
        // carries its arguments ("Method(quantity: 1)"), a fact's does not — so
        // theories must contains-match, and an exact match would find nothing.
        var theory = Method("Tests.PricingTests", "InlineTheory");

        Assert.Multiple(() =>
        {
            Assert.That(_detector.IsTestMethod(theory), Is.True);
            Assert.That(_detector.IsParameterizedTest(theory), Is.True);
        });
    }

    [Test]
    public void AttributeDerivedFromFact_IsStillATest()
    {
        // [IntegrationFact : FactAttribute] is idiomatic xUnit. Missing it would
        // classify a real test as "not a test", which reads as "not affected".
        Assert.That(_detector.IsTestMethod(Method("Tests.PricingTests", "CustomFact")), Is.True);
    }

    [Test]
    public void Constructor_OfATestClass_IsLifecycle()
    {
        // xUnit re-instantiates the class per test, so the constructor is the setup.
        Assert.That(_detector.GetLifecycleScope(Method("Tests.PricingTests", ".ctor")),
            Is.EqualTo(TestLifecycleScope.Fixture));
    }

    [Test]
    public void Dispose_OfATestClass_IsLifecycle() =>
        Assert.That(_detector.GetLifecycleScope(Method("Tests.PricingTests", "Dispose")),
            Is.EqualTo(TestLifecycleScope.Fixture));

    [Test]
    public void Constructor_OfAPlainClass_IsNotLifecycle()
    {
        // The rule is "constructor of a class holding tests", not "any constructor" —
        // otherwise every type in the build would look like a test fixture.
        Assert.That(_detector.GetLifecycleScope(Method("Tests.NotATestClass", ".ctor")),
            Is.EqualTo(TestLifecycleScope.None));
    }

    [Test]
    public void OrdinaryMethod_OfATestClass_IsNotLifecycle() =>
        Assert.That(_detector.GetLifecycleScope(Method("Tests.PricingTests", "Helper")),
            Is.EqualTo(TestLifecycleScope.None));

    [Test]
    public void MemberData_ResolvesToTheDeclaringType()
    {
        var sources = _detector.GetTestCaseSources(Method("Tests.PricingTests", "MemberTheory")).ToList();

        Assert.That(sources, Is.EqualTo(new[] { new TestCaseSourceRef("Tests.PricingTests", "Cases") }));
    }

    [Test]
    public void MemberData_WithMemberType_ResolvesToThatTypeInstead()
    {
        var sources = _detector.GetTestCaseSources(Method("Tests.PricingTests", "ExternalMemberTheory")).ToList();

        Assert.That(sources, Is.EqualTo(new[] { new TestCaseSourceRef("Tests.CaseData", "Shared") }));
    }

    [Test]
    public void ClassData_TakesEveryMemberOfTheDataType()
    {
        var sources = _detector.GetTestCaseSources(Method("Tests.PricingTests", "ClassTheory")).ToList();

        Assert.That(sources, Is.EqualTo(new[] { new TestCaseSourceRef("Tests.CaseData", "*") }));
    }

    [Test]
    public void ClassFixture_IsReportedAsExternallyConstructed()
    {
        var fixtures = _detector.GetExternallyConstructedFixtures(Type("Tests.FixtureConsumer"))
            .Select(t => t.FullName)
            .ToList();

        Assert.That(fixtures, Is.EqualTo(new[] { "Tests.SharedFixture" }));
    }

    [Test]
    public void PlainClass_HasNoExternallyConstructedFixtures() =>
        Assert.That(_detector.GetExternallyConstructedFixtures(Type("Tests.NotATestClass")), Is.Empty);

    [Test]
    public void NUnitDetector_DoesNotClaimXunitTests()
    {
        // The composite takes a union, so the two must not overlap or an NUnit-only
        // solution would start reporting xUnit lifecycle methods.
        var nunit = new NUnitTestDetector();

        Assert.Multiple(() =>
        {
            Assert.That(nunit.IsTestMethod(Method("Tests.PricingTests", "PlainFact")), Is.False);
            Assert.That(nunit.GetLifecycleScope(Method("Tests.PricingTests", ".ctor")),
                Is.EqualTo(TestLifecycleScope.None));
        });
    }

    [Test]
    public void Registry_ResolvesXunitToTheVsTestDialect()
    {
        var xunit = TestFrameworks.ByName("xunit")!;

        Assert.Multiple(() =>
        {
            Assert.That(xunit, Is.TypeOf<XunitTestDetector>());
            Assert.That(xunit.Dialect, Is.EqualTo(TestFilterDialect.VsTest));
            Assert.That(xunit.MarkerAssemblies, Does.Contain("xunit.core"));
        });
    }
}
