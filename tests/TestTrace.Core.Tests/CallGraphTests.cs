using NUnit.Framework;
using TestTrace.Core;

namespace TestTrace.Core.Tests;

// A miniature DI shape inside this test assembly: the test only touches the
// interface and the consumer; the implementation must still be reachable in
// reverse when its body changes.
internal interface IGadget
{
    int Work(int x);
}

internal sealed class Gadget : IGadget
{
    public int Work(int x) => x + 1;
}

internal sealed class GadgetConsumer(IGadget gadget)
{
    public int Run() => gadget.Work(1);
}

[TestFixture]
public class GadgetConsumerTests
{
    [Test]
    public void Run_AddsOne()
    {
        Assert.That(new GadgetConsumer(new Gadget()).Run(), Is.EqualTo(2));
    }
}

[TestFixture]
public class CallGraphTests
{
    private static CallGraphIndex BuildOwnGraph() =>
        CallGraphBuilder.Build([typeof(CallGraphTests).Assembly.Location], new NUnitTestDetector());

    [Test]
    public void InterfaceImplChange_ReachesTestThatOnlyTouchesInterface()
    {
        var graph = BuildOwnGraph();
        var changed = $"System.Int32 {typeof(Gadget).FullName}::Work(System.Int32)";

        var selected = GraphWalker.SelectTests(graph, [changed]);

        Assert.That(selected.Select(t => t.DisplayName),
            Has.Member($"{typeof(GadgetConsumerTests).FullName}.Run_AddsOne"));
    }

    [Test]
    public void SelectedTest_CarriesReasonAndChainThroughInterfaceHop()
    {
        var graph = BuildOwnGraph();
        var changed = $"System.Int32 {typeof(Gadget).FullName}::Work(System.Int32)";

        var test = GraphWalker.SelectTests(graph, [changed])
            .Single(t => t.DisplayName == $"{typeof(GadgetConsumerTests).FullName}.Run_AddsOne");

        Assert.Multiple(() =>
        {
            Assert.That(test.Reason, Does.Contain("changed method"));
            Assert.That(test.Chain.First(), Is.EqualTo($"{typeof(Gadget).FullName}::Work/1"),
                "chain starts at the changed method");
            Assert.That(test.Chain, Has.Member($"{typeof(IGadget).FullName}::Work/1"),
                "chain passes through the interface method hop");
            Assert.That(test.Chain.Last(), Is.EqualTo(test.Chain.Last()).And.Contains("Run_AddsOne"),
                "chain ends at the test");
        });
    }

    [Test]
    public void ChangedTestMethod_SelectsItself()
    {
        var graph = BuildOwnGraph();
        var changed = $"System.Void {typeof(GadgetConsumerTests).FullName}::Run_AddsOne()";

        var selected = GraphWalker.SelectTests(graph, [changed]);

        Assert.That(selected.Select(t => t.DisplayName),
            Has.Member($"{typeof(GadgetConsumerTests).FullName}.Run_AddsOne"));
    }

    [Test]
    public void UnrelatedChange_SelectsNothing()
    {
        var graph = BuildOwnGraph();
        // HasherFixture.PlainAdd has no callers and is not a test.
        var changed = $"System.Int32 {typeof(HasherFixture).FullName}::PlainAdd(System.Int32,System.Int32)";

        var selected = GraphWalker.SelectTests(graph, [changed]);

        Assert.That(selected, Is.Empty);
    }

    [Test]
    public void TestNodes_AreDetectedWithAssembly()
    {
        var graph = BuildOwnGraph();

        var run = graph.Tests.SingleOrDefault(t =>
            t.DisplayName == $"{typeof(GadgetConsumerTests).FullName}.Run_AddsOne");
        Assert.Multiple(() =>
        {
            Assert.That(run, Is.Not.Null);
            Assert.That(run!.Assembly, Is.EqualTo("TestTrace.Core.Tests"));
        });
    }

    [Test]
    public void AlwaysRun_PinsMatchingTests()
    {
        var graph = BuildOwnGraph();

        var pinned = GraphWalker.SelectAlwaysRun(graph, ["*GadgetConsumerTests*"]);

        Assert.Multiple(() =>
        {
            Assert.That(pinned.Select(t => t.DisplayName),
                Has.Member($"{typeof(GadgetConsumerTests).FullName}.Run_AddsOne"));
            Assert.That(pinned, Has.All.Matches<SelectedTest>(t => t.Reason.Contains("--always-run")));
        });
    }

    [Test]
    public void MethodKeys_ParseChangedFqnAndTypeEntries()
    {
        Assert.Multiple(() =>
        {
            Assert.That(
                MethodKeys.FromChangedFqn(
                    "System.Threading.Tasks.Task`1<SampleApp.Services.OrderReceipt> SampleApp.Services.OrderService::PlaceAsync(SampleApp.Domain.Order)",
                    out _),
                Is.EqualTo("SampleApp.Services.OrderService::PlaceAsync/1"));
            Assert.That(
                MethodKeys.FromChangedFqn("System.Int32 T::M(System.Func`2<System.Int32,System.String>,System.Int32)", out _),
                Is.EqualTo("T::M/2"),
                "nested generic commas must not inflate the param count");
            Assert.That(
                MethodKeys.FromChangedFqn("SampleApp.Domain.Customer::<type>", out var typeNode),
                Is.EqualTo("TN:SampleApp.Domain.Customer"));
            Assert.That(typeNode, Is.EqualTo("SampleApp.Domain.Customer"));
        });
    }
}
