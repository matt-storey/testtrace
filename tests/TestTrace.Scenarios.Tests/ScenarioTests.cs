using NUnit.Framework;
using TestTrace.Cli;
using TestTrace.Core;

namespace TestTrace.Scenarios.Tests;

/// <summary>
/// End-to-end verification: build the sample, snapshot it, mutate a source file,
/// rebuild, and check the selected tests against the scenario's expectations.
///
/// Expectations are must-include / must-not-include lists rather than exact
/// equality: over-approximation is allowed by design, and the must-not-include
/// list is what stops the tool degenerating into "always run everything".
///
/// One workspace per TFM is built once and reused; each scenario reverts to the
/// baseline state before applying its own edits.
/// </summary>
[TestFixture("net8.0")]
[TestFixture("net10.0")]
[NonParallelizable]
public sealed class ScenarioTests
{
    private readonly string _tfm;
    private SampleWorkspace _workspace = null!;
    private Manifest _baseline = null!;

    public ScenarioTests(string tfm) => _tfm = tfm;

    [OneTimeSetUp]
    public void BuildBaseline()
    {
        _workspace = SampleWorkspace.Create(_tfm);
        _workspace.Build();
        _baseline = AssemblyScanner.Snapshot(_workspace.Harvest("baseline"));
    }

    [OneTimeTearDown]
    public void Cleanup() => _workspace.Dispose();

    [SetUp]
    public void RestorePristineSources() => _workspace.RestoreSources();

    /// <summary>
    /// Both front-ends are checked from a single build: the mutation and rebuild are
    /// the expensive part, and the manifest and PDB paths differ only in whether a
    /// baseline is supplied.
    /// </summary>
    [TestCaseSource(typeof(Scenarios), nameof(Scenarios.All))]
    public void Selects_ExpectedTests(Scenario scenario)
    {
        var diff = _workspace.ApplyEdits(scenario);
        _workspace.Build();
        var current = _workspace.Harvest("current");

        // The diff goes in as-is: the analyzer parses `git diff` output itself, so
        // this exercises the same input path a real caller uses.
        var changedFiles = diff.Split('\n');

        var framework = TestFrameworks.ByName(scenario.Framework)!;

        var manifestResult = Analyzer.Analyze(_baseline, [current], framework, changedFiles);
        AssertExpectations(scenario, manifestResult, "manifest", allowDeviation: false);

        var pdbResult = Analyzer.Analyze(
            baseline: null, currentDirectories: [current], framework: framework, changedFiles: changedFiles);
        AssertExpectations(scenario, pdbResult, "pdb", allowDeviation: scenario.PdbDeviates);

        // Filters must be emitted for whatever was selected, and never for an
        // assembly with no selected tests (vstest fails on a zero-match filter).
        if (!manifestResult.RunEverything && manifestResult.Selection.Count > 0)
        {
            var filters = FilterEmitter.Emit(manifestResult.Selection, [current], 200, framework);
            Assert.That(filters, Is.Not.Empty);
            Assert.That(filters, Has.All.Matches<AssemblyFilter>(f => f.TestCount > 0));
            Assert.That(filters, Has.All.Matches<AssemblyFilter>(
                f => f.Dialect == framework.Dialect.ToString()));
        }
    }

    /// <summary>
    /// The force-full-run escape hatch only runs when the caller passes
    /// --changed-files. Manifest mode must not depend on that: a config edit changes
    /// no IL, so the assembly diff is empty and the result used to be "no
    /// assembly-level changes" — exit 3, skip the entire suite, for a real
    /// behavioural change. The README's own quickstart omits --changed-files.
    /// </summary>
    [Test]
    public void ConfigChange_WithoutChangedFiles_StillFailsOpen()
    {
        var scenario = Scenarios.All.Single(s => s.Name == "config-change");
        _workspace.ApplyEdits(scenario);
        _workspace.Build();
        var current = _workspace.Harvest("current");

        var result = Analyzer.Analyze(_baseline, [current], Nunit);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunEverything, Is.True, result.Reason);
            Assert.That(result.Reason, Does.Contain("appsettings.json"));
        });
    }

    /// <summary>Counterweight: content-file tracking must not make every run fail open.
    /// An unmodified rebuild still has to select nothing.</summary>
    [Test]
    public void UnchangedRebuild_WithoutChangedFiles_SelectsNothing()
    {
        _workspace.Build();
        var current = _workspace.Harvest("current");

        var result = Analyzer.Analyze(_baseline, [current], Nunit);

        Assert.Multiple(() =>
        {
            Assert.That(result.RunEverything, Is.False, result.Reason);
            Assert.That(result.SelectedTests, Is.Empty);
        });
    }

    private static readonly ITestFrameworkDetector Nunit = TestFrameworks.ByName("nunit")!;

    /// <summary>
    /// One changed method, one build, three analyses — the replacement for the old
    /// cross-framework assertion. Each framework must see its OWN affected tests and
    /// none of the others', which is what proves per-framework discovery is wired up
    /// rather than one framework's graph being served to all of them.
    /// </summary>
    [Test]
    public void MixedSolution_EachFrameworkSeesItsOwnTests()
    {
        _workspace.ApplyEdits(Scenarios.All.Single(s => s.Name == "single-method-body-change"));
        _workspace.Build();
        var current = _workspace.Harvest("current");

        var expected = new (string Framework, string Present, string Absent)[]
        {
            ("nunit", "SampleApp.Services.Tests.OrderServiceTests.", "SampleApp.Xunit.Tests."),
            ("xunit", "SampleApp.Xunit.Tests.OrderPricingTests.", "SampleApp.Services.Tests."),
            ("tunit", "SampleApp.TUnit.Tests.PricingTests.", "SampleApp.Xunit.Tests."),
            ("mstest", "SampleApp.MSTest.Tests.OrderTotalTests.", "SampleApp.Xunit.Tests."),
        };

        Assert.Multiple(() =>
        {
            foreach (var (name, present, absent) in expected)
            {
                var result = Analyzer.Analyze(_baseline, [current], TestFrameworks.ByName(name)!);
                Assert.That(result.RunEverything, Is.False, $"{name}: {result.Reason}");
                Assert.That(result.SelectedTests, Has.Some.StartsWith(present), $"{name} saw none of its own tests");
                Assert.That(result.SelectedTests, Has.None.StartsWith(absent), $"{name} leaked another framework's tests");
            }
        });
    }

    private static void AssertExpectations(Scenario scenario, AnalyzeResult result, string frontEnd, bool allowDeviation)
    {
        var selected = result.SelectedTests;
        var failures = new List<string>();

        if (scenario.ExpectRunEverything)
        {
            if (!result.RunEverything)
                failures.Add($"expected RUN_EVERYTHING, got {selected.Count} selected test(s)");
        }
        else if (result.RunEverything)
        {
            // Over-approximation is allowed unless the scenario pins something down.
            if (scenario.MustNotInclude.Length > 0)
                failures.Add($"fell back to RUN_EVERYTHING ({result.Reason}), violating must-not-include");
        }
        else
        {
            foreach (var pattern in scenario.MustInclude)
            {
                if (!selected.Any(t => Matches(t, pattern)))
                    failures.Add($"must-include not matched: {pattern}");
            }

            foreach (var pattern in scenario.MustNotInclude)
            {
                var hits = selected.Where(t => Matches(t, pattern)).ToList();
                if (hits.Count > 0)
                    failures.Add($"must-not-include matched: {pattern} -> {hits[0]}" +
                                 (hits.Count > 1 ? $" (+{hits.Count - 1} more)" : ""));
            }

            foreach (var pattern in scenario.KnownMiss)
            {
                if (selected.Any(t => Matches(t, pattern)))
                    failures.Add($"known-miss pattern is now selected ({pattern}); " +
                                 "if the limitation was fixed, promote it to MustInclude");
            }
        }

        if (allowDeviation)
        {
            // Asserted to STILL deviate: if the degradation is ever fixed, this fails
            // and tells you to drop the flag rather than passing unnoticed.
            Assert.That(failures, Is.Not.Empty,
                $"scenario '{scenario.Name}' is marked PdbDeviates but now meets its expectations under the " +
                "PDB front-end — remove the flag");
            return;
        }

        Assert.That(failures, Is.Empty,
            $"scenario '{scenario.Name}' [{frontEnd}] ({result.Reason}):\n  " + string.Join("\n  ", failures));
    }

    /// <summary>Glob match over a fully qualified test name.</summary>
    private static bool Matches(string value, string pattern)
    {
        if (pattern == "*")
            return true;
        var regex = "^" + string.Join("", pattern.Select(c => c switch
        {
            '*' => ".*",
            '?' => ".",
            _ => System.Text.RegularExpressions.Regex.Escape(c.ToString()),
        })) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(value, regex);
    }
}
