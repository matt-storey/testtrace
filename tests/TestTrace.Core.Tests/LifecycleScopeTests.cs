using Mono.Cecil;
using NUnit.Framework;
using TestTrace.Core;

namespace TestTrace.Core.Tests.InertNamespace
{
    /// <summary>
    /// A real NUnit [SetUpFixture], used as the fixture for the scope test below. Its
    /// one-time hooks apply to every test in this namespace — which deliberately holds
    /// none, so running it changes nothing.
    /// </summary>
    [SetUpFixture]
    public class NamespaceSetup
    {
        [OneTimeSetUp]
        public void BeforeNamespace() { }
    }
}

namespace TestTrace.Core.Tests
{
    /// <summary>
    /// Lifecycle methods reach as far as the framework runs them. Getting the scope wrong
    /// under-selects: an [AssemblyInitialize] treated as fixture-scoped used to select only
    /// the tests of whichever class happened to declare it.
    /// </summary>
    [TestFixture]
    public class LifecycleScopeTests
    {
        private static ModuleDefinition _self = null!;

        [OneTimeSetUp]
        public void ReadOwnAssembly() =>
            _self = ModuleDefinition.ReadModule(typeof(LifecycleScopeTests).Assembly.Location);

        [OneTimeTearDown]
        public void Cleanup() => _self.Dispose();

        private static MethodDefinition SelfMethod(string typeFullName, string name) =>
            _self.GetType(typeFullName).Methods.First(m => m.Name == name);

        [Test]
        public void NUnit_OneTimeSetUp_InATestFixture_IsFixtureScoped()
        {
            // This very class: [OneTimeSetUp] in an ordinary fixture stays fixture-scoped.
            // If this widened, every NUnit lifecycle change would select whole assemblies.
            var scope = new NUnitTestDetector().GetLifecycleScope(
                SelfMethod("TestTrace.Core.Tests.LifecycleScopeTests", "ReadOwnAssembly"));

            Assert.That(scope, Is.EqualTo(TestLifecycleScope.Fixture));
        }

        [Test]
        public void NUnit_OneTimeSetUp_InASetUpFixture_IsWiderThanTheFixture()
        {
            // The same attribute means something different here: a [SetUpFixture] holds
            // hooks for a whole NAMESPACE. Widened to the assembly — an over-approximation,
            // which is the safe direction and far simpler than matching namespaces.
            var scope = new NUnitTestDetector().GetLifecycleScope(
                SelfMethod("TestTrace.Core.Tests.InertNamespace.NamespaceSetup", "BeforeNamespace"));

            Assert.That(scope, Is.EqualTo(TestLifecycleScope.Assembly));
        }

        // -- how the walker acts on a scope ---------------------------------------

        private const string Lifecycle = "Suite.Hooks::Init/0";

        private static CallGraphIndex GraphWith(TestLifecycleScope scope) => new()
        {
            Tests =
            [
                Node("Alpha", "Suite.Alpha", "A.Tests"),
                Node("Beta", "Suite.Beta", "A.Tests"),
                Node("Gamma", "Suite.Gamma", "B.Tests"),
            ],
            SetupFixtureByKey =
            {
                [Lifecycle] = new LifecycleTarget
                {
                    Scope = scope,
                    DeclaringType = "Suite.Alpha",
                    Assembly = "A.Tests",
                },
            },
        };

        private static TestNode Node(string name, string type, string assembly) => new()
        {
            Key = $"{type}::{name}/0",
            DisplayName = $"{type}.{name}",
            DeclaringType = type,
            Assembly = assembly,
            ClassName = type[(type.LastIndexOf('.') + 1)..],
        };

        /// <summary>The changed method IS the lifecycle method, which is the shape a real
        /// edit to a setup body produces.</summary>
        private static List<string> SelectFor(TestLifecycleScope scope) =>
            GraphWalker.SelectTests(GraphWith(scope), ["System.Void Suite.Hooks::Init()"])
                .Select(t => t.DisplayName)
                .Order(StringComparer.Ordinal)
                .ToList();

        [Test]
        public void FixtureScope_SelectsOnlyTheDeclaringTypesTests() =>
            Assert.That(SelectFor(TestLifecycleScope.Fixture), Is.EqualTo(new[] { "Suite.Alpha.Alpha" }));

        [Test]
        public void AssemblyScope_SelectsEveryTestInThatAssemblyOnly()
        {
            // The defect this fixes: Beta lives in another class of the same assembly and
            // was previously missed entirely. Gamma is a different assembly and must stay out.
            Assert.That(SelectFor(TestLifecycleScope.Assembly),
                Is.EqualTo(new[] { "Suite.Alpha.Alpha", "Suite.Beta.Beta" }));
        }

        [Test]
        public void GlobalScope_SelectsEveryTestEverywhere() =>
            Assert.That(SelectFor(TestLifecycleScope.Global),
                Is.EqualTo(new[] { "Suite.Alpha.Alpha", "Suite.Beta.Beta", "Suite.Gamma.Gamma" }));
    }
}
