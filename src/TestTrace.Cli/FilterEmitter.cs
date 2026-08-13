using System.Security;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Utilities;
using TestTrace.Core;

namespace TestTrace.Cli;

public static class FilterEmitter
{
    /// <summary>
    /// Group the selection by containing test assembly and build one filter per
    /// assembly, in the dialect the chosen framework's runner speaks.
    ///
    /// Every test in the selection already belongs to that framework — discovery ran
    /// its detector alone — so there is nothing to filter out here.
    /// </summary>
    public static List<AssemblyFilter> Emit(
        IReadOnlyList<SelectedTest> selection,
        IReadOnlyList<string> currentDirectories,
        int maxClauses,
        ITestFrameworkDetector framework)
    {
        var dllsByName = AssemblyScanner.EnumerateAssemblies(currentDirectories)
            .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key!, g => g.Order(StringComparer.Ordinal).First(), StringComparer.OrdinalIgnoreCase);

        var filters = new List<AssemblyFilter>();
        foreach (var group in selection.GroupBy(t => t.Assembly).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var tests = group.DistinctBy(t => t.DisplayName).OrderBy(t => t.DisplayName, StringComparer.Ordinal).ToList();
            var filter = new AssemblyFilter
            {
                Assembly = group.Key,
                Dll = dllsByName.GetValueOrDefault(group.Key),
                TestCount = tests.Count,
                Dialect = framework.Dialect.ToString(),
            };

            if (tests.Count > maxClauses)
            {
                filter.RunWholeAssembly = true;
            }
            else
            {
                filter.Filter = framework.Dialect switch
                {
                    TestFilterDialect.TreeNode => TreeNodeFilter(group.Key, tests),
                    _ => VsTestFilter(tests),
                };
            }

            filters.Add(filter);
        }

        return filters;
    }

    /// <summary>
    /// VSTest expression, for NUnit and xUnit. Parameterized tests contains-match
    /// (their runtime FQNs carry argument lists); plain tests equal-match.
    /// </summary>
    private static string VsTestFilter(IReadOnlyList<SelectedTest> tests) =>
        string.Join("|", tests.Select(t =>
            t.Parameterized
                ? $"FullyQualifiedName~{FilterHelper.Escape(t.DisplayName)}"
                : $"FullyQualifiedName={FilterHelper.Escape(t.DisplayName)}"));

    /// <summary>
    /// Microsoft.Testing.Platform tree path, for TUnit:
    /// <c>/Assembly/*/(ClassA|ClassB)/(TestA|TestB*)</c>.
    ///
    /// Shape verified against a real TUnit run, and the details matter:
    ///   - Alternation is per SEGMENT and must be parenthesised. A bare
    ///     "/a/b/c|/d/e/f" does not mean "or" — it silently matched EVERY test in the
    ///     assembly, which would look like a working filter while running the lot.
    ///   - Only one --treenode-filter argument is accepted, so the whole selection has
    ///     to collapse into a single expression.
    ///   - The namespace segment is wildcarded. Pinning it would need a third
    ///     alternation group without excluding anything the class group does not
    ///     already exclude.
    ///   - Parameterized tests get a trailing '*': their node names carry the argument
    ///     list ("WithArguments(1, 2)"), exactly as xUnit theories do.
    ///
    /// Segment alternation is a cross product, so two classes in one assembly sharing
    /// a test method name will both run. That is over-selection, the safe direction,
    /// and the same trade the VSTest contains-match already makes.
    /// </summary>
    private static string TreeNodeFilter(string assembly, IReadOnlyList<SelectedTest> tests)
    {
        var classes = tests
            .Select(t => t.ClassName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        var names = tests
            .Select(t => TestNodeName(t) + (t.Parameterized ? "*" : ""))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

        return $"/{assembly}/*/{Alternation(classes)}/{Alternation(names)}";
    }

    /// <summary>The leaf segment is the method name alone; DisplayName carries the
    /// declaring type, which occupies its own segment.</summary>
    private static string TestNodeName(SelectedTest test)
    {
        var dot = test.DisplayName.LastIndexOf('.');
        return dot >= 0 ? test.DisplayName[(dot + 1)..] : test.DisplayName;
    }

    private static string Alternation(IReadOnlyList<string> values) =>
        values.Count == 0 ? "*" : $"({string.Join("|", values)})";

    public static string ToRunSettings(AssemblyFilter filter)
    {
        var content = filter.RunWholeAssembly ? "" : SecurityElement.Escape(filter.Filter);
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <RunSettings>
              <RunConfiguration>
                <TestCaseFilter>{content}</TestCaseFilter>
              </RunConfiguration>
            </RunSettings>
            """;
    }
}
