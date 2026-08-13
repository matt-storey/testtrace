using NUnit.Framework;
using TestTrace.Cli;
using TestTrace.Core;

namespace TestTrace.Core.Tests;

/// <summary>
/// Filter syntax is where a mistake is silent: a malformed expression still runs, it
/// just runs the wrong set. Every shape asserted here was verified against the real
/// runner before being encoded.
/// </summary>
[TestFixture]
public class FilterEmitterTests
{
    private static SelectedTest Test(
        string assembly, string ns, string className, string method,
        bool parameterized = false) => new()
    {
        DisplayName = $"{ns}.{className}.{method}",
        Assembly = assembly,
        DeclaringType = $"{ns}.{className}",
        Namespace = ns,
        ClassName = className,
        Parameterized = parameterized,
    };

    private static List<AssemblyFilter> Emit(
        IReadOnlyList<SelectedTest> selection, string framework, int maxClauses = 200) =>
        FilterEmitter.Emit(selection, [AppContext.BaseDirectory], maxClauses,
            TestFrameworks.ByName(framework)!);

    // -- VSTest dialect (NUnit, xUnit) ---------------------------------------

    [Test]
    public void VsTest_PlainTestEqualMatches_ParameterizedContainsMatches()
    {
        var filters = Emit(
        [
            Test("A.Tests", "A.Tests", "Suite", "Plain"),
            Test("A.Tests", "A.Tests", "Suite", "Cases", parameterized: true),
        ], "nunit");

        var filter = filters.Single().Filter;

        Assert.Multiple(() =>
        {
            Assert.That(filter, Does.Contain("FullyQualifiedName=A.Tests.Suite.Plain"));
            // Parameterized names carry an argument list at runtime, so '=' would miss.
            Assert.That(filter, Does.Contain("FullyQualifiedName~A.Tests.Suite.Cases"));
            Assert.That(filters.Single().Dialect, Is.EqualTo("VsTest"));
        });
    }

    [Test]
    public void MsTest_DataDrivenTestStillEqualMatches()
    {
        // MSTest is the exception: its data-row arguments stay out of
        // FullyQualifiedName, so '=' matches the method and runs every row. Emitting
        // '~' here would work but over-select on any name that is a prefix of another.
        var mstest = TestFrameworks.ByName("mstest")!;
        var selection = new[] { Test("M.Tests", "M.Tests", "Suite", "WithRows") };

        var filter = FilterEmitter.Emit(selection, [AppContext.BaseDirectory], 200, mstest).Single();

        Assert.Multiple(() =>
        {
            Assert.That(filter.Filter, Is.EqualTo("FullyQualifiedName=M.Tests.Suite.WithRows"));
            Assert.That(filter.Dialect, Is.EqualTo("VsTest"));
        });
    }

    [Test]
    public void VsTest_GroupsPerAssembly()
    {
        var filters = Emit(
        [
            Test("A.Tests", "A.Tests", "Suite", "One"),
            Test("B.Tests", "B.Tests", "Suite", "Two"),
        ], "nunit");

        Assert.That(filters.Select(f => f.Assembly), Is.EqualTo(new[] { "A.Tests", "B.Tests" }));
    }

    // -- TreeNode dialect (TUnit) --------------------------------------------

    [Test]
    public void TreeNode_UsesParenthesisedAlternationPerSegment()
    {
        var filters = Emit(
        [
            Test("T.Tests", "T.Tests", "Delivery", "Fast"),
            Test("T.Tests", "T.Tests", "Delivery", "Slow"),
        ], "tunit");

        Assert.Multiple(() =>
        {
            Assert.That(filters.Single().Filter, Is.EqualTo("/T.Tests/*/(Delivery)/(Fast|Slow)"));
            Assert.That(filters.Single().Dialect, Is.EqualTo("TreeNode"));
        });
    }

    [Test]
    public void TreeNode_ParameterizedTestGetsWildcardSuffix()
    {
        var filters = Emit(
            [Test("T.Tests", "T.Tests", "Delivery", "Rows", parameterized: true)], "tunit");

        // Node names carry the arguments ("Rows(1, 2)"), so an exact leaf misses.
        Assert.That(filters.Single().Filter, Is.EqualTo("/T.Tests/*/(Delivery)/(Rows*)"));
    }

    [Test]
    public void TreeNode_AlternatesAcrossClasses()
    {
        var filters = Emit(
        [
            Test("T.Tests", "T.Tests", "Delivery", "One"),
            Test("T.Tests", "T.Tests", "Pricing", "Two"),
        ], "tunit");

        Assert.That(filters.Single().Filter, Is.EqualTo("/T.Tests/*/(Delivery|Pricing)/(One|Two)"));
    }

    [Test]
    public void TreeNode_EmitsOnePathPerAssembly_NeverATopLevelOr()
    {
        // The trap this guards: "/A/*/(..)|/B/*/(..)" is NOT an or. Verified against a
        // real TUnit run, it matches EVERY test — a silent full-suite run wearing a
        // filter's clothes. Each assembly must therefore carry its own single path.
        var filters = Emit(
        [
            Test("A.Tests", "A.Tests", "Suite", "One"),
            Test("B.Tests", "B.Tests", "Suite", "Two"),
        ], "tunit");

        Assert.That(filters, Has.Count.EqualTo(2));
        foreach (var filter in filters)
            Assert.That(HasTopLevelPipe(filter.Filter), Is.False,
                $"'{filter.Filter}' joins tree paths with '|', which matches everything");
    }

    /// <summary>A '|' outside any parenthesis group — the malformed shape.</summary>
    private static bool HasTopLevelPipe(string filter)
    {
        var depth = 0;
        foreach (var c in filter)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            else if (c == '|' && depth == 0) return true;
        }

        return false;
    }

    // -- thresholds ----------------------------------------------------------

    [Test]
    public void AboveTheClauseThreshold_TheWholeAssemblyRuns()
    {
        var many = Enumerable.Range(0, 5)
            .Select(i => Test("A.Tests", "A.Tests", "Suite", $"Test{i}"))
            .ToList();

        var filter = Emit(many, "nunit", maxClauses: 3).Single();

        Assert.Multiple(() =>
        {
            Assert.That(filter.RunWholeAssembly, Is.True);
            // Must stay empty rather than becoming a wildcard: an unmatchable filter
            // hard-fails the run instead of running everything.
            Assert.That(filter.Filter, Is.Empty);
            Assert.That(filter.TestCount, Is.EqualTo(5));
        });
    }

    [Test]
    public void EmptySelection_EmitsNoFilters() =>
        Assert.That(Emit([], "nunit"), Is.Empty);
}
