using System.CommandLine;
using System.Text.Json;
using TestTrace.Cli;
using TestTrace.Core;

var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
};

var root = new RootCommand("testtrace — IL-level test impact analysis for .NET");

// Scope: which assemblies get method-level hashing and call-graph construction.
// Everything is still content-hashed, so package bumps are always detected.
var includeAssembliesOption = new Option<string[]>("--include-assemblies")
{
    Description = "Globs of assembly names to analyze in depth (default: assemblies with a co-located .pdb, i.e. the ones you build)",
    AllowMultipleArgumentsPerToken = true,
};
var excludeAssembliesOption = new Option<string[]>("--exclude-assemblies")
{
    Description = "Globs of assembly names to exclude from in-depth analysis",
    AllowMultipleArgumentsPerToken = true,
};

AssemblyScope ScopeFrom(ParseResult parseResult) => new()
{
    Include = parseResult.GetValue(includeAssembliesOption) ?? [],
    Exclude = parseResult.GetValue(excludeAssembliesOption) ?? [],
};

// Solution mode: resolve every project's build output, and take the solution's own
// project list as the scope — a better signal than PDB adjacency, since it is
// exactly "the code in this solution".
var solutionOption = new Option<string?>("--solution")
{
    Description = "Solution file (.sln or .slnx). Resolves every project's build output; use instead of --input/--current",
};
var projectOption = new Option<string[]>("--project")
{
    Description = "One or more project files (.csproj/.fsproj/.vbproj). Resolves their build output; use instead of --input/--current",
    AllowMultipleArgumentsPerToken = true,
};
var configurationOption = new Option<string>("--configuration", "-c")
{
    Description = "Build configuration to locate outputs for (solution mode)",
    DefaultValueFactory = _ => "Release",
};
var frameworkOption = new Option<string?>("--framework", "-f")
{
    Description = "Target framework to locate outputs for, e.g. net8.0 (solution mode; required if projects multi-target)",
};

(List<string> Directories, AssemblyScope Scope) ResolveInputs(
    ParseResult parseResult, string? explicitDirectory, string optionName)
{
    var solutionPath = parseResult.GetValue(solutionOption);
    var projectPaths = parseResult.GetValue(projectOption) ?? [];
    if (solutionPath is not null && projectPaths.Length > 0)
        throw new InvalidOperationException("pass --solution or --project, not both");

    if (solutionPath is null && projectPaths.Length == 0)
    {
        if (explicitDirectory is null)
            throw new InvalidOperationException($"pass one of {optionName}, --solution or --project");
        return ([explicitDirectory], ScopeFrom(parseResult));
    }

    var configuration = parseResult.GetValue(configurationOption)!;
    var framework = parseResult.GetValue(frameworkOption);
    var load = solutionPath is not null
        ? SolutionReader.Load(solutionPath, configuration, framework)
        : SolutionReader.LoadProjects(projectPaths, configuration, framework);

    foreach (var warning in load.Warnings)
        Console.Error.WriteLine($"testtrace: warning: {warning}");

    var source = solutionPath is not null
        ? Path.GetFileName(solutionPath)
        : string.Join(", ", projectPaths.Select(Path.GetFileName));

    var directories = load.OutputDirectories;
    if (directories.Count == 0)
        throw new InvalidDataException($"no build outputs found for {source}; build it first");

    // Explicit --include/--exclude still win, so the resolved scope can be overridden.
    var explicitScope = ScopeFrom(parseResult);
    var scope = explicitScope.Include.Count > 0 || explicitScope.Exclude.Count > 0 ? explicitScope : load.ToScope();
    Console.Error.WriteLine(
        $"testtrace: {source}: {load.Projects.Count} project(s), {directories.Count} output directory(ies)");
    return (directories, scope);
}

// -- snapshot ---------------------------------------------------------------
var inputOption = new Option<string?>("--input") { Description = "Build output directory to snapshot (or use --solution)" };
var outputOption = new Option<string>("--output", "-o") { Description = "Manifest file to write", Required = true };
var snapshotCommand = new Command("snapshot", "Write a baseline manifest for a build output directory");
snapshotCommand.Options.Add(inputOption);
snapshotCommand.Options.Add(outputOption);
snapshotCommand.Options.Add(includeAssembliesOption);
snapshotCommand.Options.Add(excludeAssembliesOption);
snapshotCommand.Options.Add(solutionOption);
snapshotCommand.Options.Add(projectOption);
snapshotCommand.Options.Add(configurationOption);
snapshotCommand.Options.Add(frameworkOption);
snapshotCommand.SetAction(parseResult =>
{
    // snapshot runs on the CI side of the fence: errors should be loud, not fail-open
    // — but reported as a message, not an unhandled exception.
    try
    {
        var (directories, scope) = ResolveInputs(parseResult, parseResult.GetValue(inputOption), "--input");
        var manifest = AssemblyScanner.Snapshot(directories, scope);
        ManifestIo.Save(manifest, parseResult.GetValue(outputOption)!);
        var analyzed = manifest.Assemblies.Count(a => a.MethodsAnalyzed);
        Console.Error.WriteLine(
            $"testtrace: snapshot of {manifest.Assemblies.Count} assemblies ({manifest.Tfm}); " +
            $"{analyzed} analyzed in depth [{manifest.Scope}], {manifest.Assemblies.Count - analyzed} content-hashed only");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"testtrace: snapshot failed: {ex.Message}");
        return 1;
    }
});
root.Subcommands.Add(snapshotCommand);

// -- analyze ----------------------------------------------------------------
var manifestOption = new Option<string?>("--manifest") { Description = "Baseline manifest file (preferred)" };
var baselineOption = new Option<string?>("--baseline") { Description = "Baseline build directory (snapshotted in-memory when no manifest is given)" };
var currentOption = new Option<string?>("--current") { Description = "Current build directory (or use --solution)" };
var changedFilesOption = new Option<string?>("--changed-files") { Description = "Changed sources: one path per line with optional line ranges (path:12-34), or a unified diff; used by the escape hatch and the PDB front-end" };
var formatOption = new Option<string>("--format") { Description = "Output format: json | filter | filter-lines | runsettings", DefaultValueFactory = _ => "json" };
var assemblyOption = new Option<string?>("--assembly")
{
    Description = "Restrict filter output to this test assembly (name without .dll). Use when running one project at a time, so clauses for other projects do not zero-match",
};
var forcePathsOption = new Option<string[]>("--force-full-run-paths")
{
    Description = "Globs of changed files that force RUN_EVERYTHING (no-IL changes)",
    DefaultValueFactory = _ => Analyzer.DefaultForceFullRunGlobs,
    AllowMultipleArgumentsPerToken = true,
};
var maxClausesOption = new Option<int>("--max-filter-clauses")
{
    Description = "Above this many clauses an assembly's filter is dropped and the whole assembly runs",
    DefaultValueFactory = _ => 200,
};
var outputDirOption = new Option<string>("--output-dir")
{
    Description = "Directory for --format runsettings files",
    DefaultValueFactory = _ => ".",
};
var explainOption = new Option<bool>("--explain")
{
    Description = "Print a human-readable report: every selected test with the chain from the changed method to it",
};
var alwaysRunOption = new Option<string[]>("--always-run")
{
    Description = "Wildcard patterns of test names to select regardless of the diff (for reflection-heavy suites the static graph cannot reach)",
    AllowMultipleArgumentsPerToken = true,
};

// Required, and deliberately not inferred. Detection spans every framework, but the
// runners' filter languages are mutually unintelligible — vstest expressions mean
// nothing to Microsoft.Testing.Platform and vice versa — so the caller has to say
// which runner the output is destined for.
var testFrameworkOption = new Option<string>("--test-framework")
{
    Description = $"Test framework whose filter syntax to emit: {string.Join(" | ", TestFrameworks.Names)}",
    Required = true,
};
testFrameworkOption.Validators.Add(result =>
{
    var value = result.GetValueOrDefault<string>();
    if (TestFrameworks.ByName(value) is null)
        result.AddError($"unknown test framework '{value}'; expected one of: {string.Join(", ", TestFrameworks.Names)}");
});

var analyzeCommand = new Command("analyze", "Compute the set of tests affected by the baseline->current difference");
analyzeCommand.Options.Add(manifestOption);
analyzeCommand.Options.Add(baselineOption);
analyzeCommand.Options.Add(currentOption);
analyzeCommand.Options.Add(changedFilesOption);
analyzeCommand.Options.Add(formatOption);
analyzeCommand.Options.Add(forcePathsOption);
analyzeCommand.Options.Add(maxClausesOption);
analyzeCommand.Options.Add(outputDirOption);
analyzeCommand.Options.Add(explainOption);
analyzeCommand.Options.Add(alwaysRunOption);
analyzeCommand.Options.Add(testFrameworkOption);
analyzeCommand.Options.Add(assemblyOption);
analyzeCommand.Options.Add(includeAssembliesOption);
analyzeCommand.Options.Add(excludeAssembliesOption);
analyzeCommand.Options.Add(solutionOption);
analyzeCommand.Options.Add(projectOption);
analyzeCommand.Options.Add(configurationOption);
analyzeCommand.Options.Add(frameworkOption);
analyzeCommand.SetAction(parseResult =>
{
    AnalyzeResult result;
    List<string> currentDirs = [];
    // Validated by the option's validator, so this cannot be null here.
    var testFramework = TestFrameworks.ByName(parseResult.GetValue(testFrameworkOption))!;
    try
    {
        // A corrupt or missing baseline is not fatal: warn and fall through to the
        // next front-end in the chain (PDB line map, then RUN_EVERYTHING).
        var (resolvedDirs, scope) = ResolveInputs(parseResult, parseResult.GetValue(currentOption), "--current");
        currentDirs = resolvedDirs;

        Manifest? baseline = null;
        var manifestPath = parseResult.GetValue(manifestOption);
        var baselineDir = parseResult.GetValue(baselineOption);
        try
        {
            baseline = manifestPath is not null
                ? ManifestIo.Load(manifestPath)
                : baselineDir is not null
                    ? AssemblyScanner.Snapshot(baselineDir, scope)
                    : null;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                $"testtrace: cannot read baseline ({ex.Message}); falling through to the next front-end");
        }

        var changedFilesPath = parseResult.GetValue(changedFilesOption);
        var changedFiles = changedFilesPath is not null
            ? File.ReadAllLines(changedFilesPath).Where(l => l.Length > 0).ToList()
            : null;

        result = Analyzer.Analyze(
            baseline, currentDirs, testFramework, changedFiles,
            parseResult.GetValue(forcePathsOption),
            parseResult.GetValue(alwaysRunOption),
            scope);

        if (result.FrontEnd == "pdb")
            Console.Error.WriteLine("testtrace: no baseline manifest — using the degraded PDB line-map front-end");
        foreach (var warning in result.Warnings)
            Console.Error.WriteLine($"testtrace: warning: {warning}");

        if (!result.RunEverything && result.Selection.Count > 0)
            result.Filters = FilterEmitter.Emit(
                result.Selection, currentDirs, parseResult.GetValue(maxClausesOption), testFramework);
    }
    catch (Exception ex)
    {
        // Fail open: any error means RUN_EVERYTHING, never an empty selection.
        Console.Error.WriteLine($"testtrace: {ex.Message}; falling back to RUN_EVERYTHING");
        result = AnalyzeResult.Everything(ex.Message);
    }

    var assemblyName = parseResult.GetValue(assemblyOption);
    var filters = assemblyName is null
        ? result.Filters
        : result.Filters.Where(f => string.Equals(f.Assembly, assemblyName, StringComparison.OrdinalIgnoreCase)).ToList();

    // Exit 3 ("nothing to run, skip the run entirely") is a property of the RESULT, not
    // of the output format, so every format reports it. Anything runnable — including
    // RUN_EVERYTHING — is 0.
    var exitCode = !result.RunEverything && filters.Count == 0 ? 3 : 0;

    if (parseResult.GetValue(explainOption))
    {
        Explain.Print(result, Console.Out);
        return exitCode;
    }

    switch (parseResult.GetValue(formatOption))
    {
        // A bare filter string on stdout, ready for: dotnet test --filter "$filter".
        // Contract, chosen so the naive two-liner is always CORRECT (never misses a
        // test) and the exit code lets callers make it optimal:
        //   RUN_EVERYTHING  -> empty output, exit 0. `--filter ""` runs everything.
        //   selection       -> filter string,  exit 0.
        //   nothing to run  -> empty output, exit 3. Emitting a zero-match filter
        //                      instead would hard-fail the run with "No test matches".
        case "filter":
            if (!result.RunEverything && exitCode != 3)
            {
                if (filters.Any(f => f.RunWholeAssembly))
                    break; // over threshold: empty filter, run the whole assembly

                // Tree paths must never be joined with '|'. Verified against a real
                // TUnit run: "/A/*/(..)|/B/*/(..)" is not an OR — it matches EVERY
                // test in the run, so the caller would get a silent full-suite run
                // wearing a filter's clothes. VSTest expressions do OR with '|', so
                // this only applies to the TreeNode dialect.
                if (filters.Count > 1 && filters.Any(f => f.Dialect == nameof(TestFilterDialect.TreeNode)))
                {
                    Console.Error.WriteLine(
                        $"testtrace: {filters.Count} assemblies have tree-path filters, which cannot be " +
                        "combined into one expression; use --format filter-lines, or --assembly <name> " +
                        $"to pick one of: {string.Join(", ", filters.Select(f => f.Assembly))}");
                    return 2;
                }

                Console.WriteLine(string.Join("|", filters.Select(f => f.Filter)));
            }
            break;

        // One line per assembly: "<dll><TAB><dialect><TAB><filter>". The dialect column
        // tells the caller which runner and which flag the expression belongs to —
        // VsTest goes to `dotnet vstest --TestCaseFilter:`, TreeNode to the test
        // executable's `--treenode-filter`.
        //
        // An EMPTY filter field means "run this assembly whole". It must never be a
        // wildcard: vstest filters have no glob syntax, so `--TestCaseFilter:"*"` is a
        // contains-match for a literal asterisk, matches nothing, and hard-fails the
        // run with "No test matches the given testcase filter" — running zero tests
        // where the intent was to run all of them.
        case "filter-lines":
            if (result.RunEverything)
            {
                Console.WriteLine("RUN_EVERYTHING");
            }
            else if (exitCode != 3)
            {
                foreach (var filter in filters)
                    Console.WriteLine(
                        $"{filter.Dll ?? filter.Assembly}\t{filter.Dialect}\t{(filter.RunWholeAssembly ? "" : filter.Filter)}");
            }
            break;

        case "runsettings":
            if (result.RunEverything)
            {
                Console.WriteLine("RUN_EVERYTHING");
            }
            else if (filters.Any(f => f.Dialect == nameof(TestFilterDialect.TreeNode)))
            {
                // .runsettings is a VSTest concept; Microsoft.Testing.Platform runners
                // ignore <TestCaseFilter> entirely. Writing the file would produce a
                // run with no filter at all, silently running everything.
                Console.Error.WriteLine(
                    "testtrace: --format runsettings is VSTest-only and does not apply to " +
                    $"{parseResult.GetValue(testFrameworkOption)}; use --format filter or filter-lines");
                return 2;
            }
            else
            {
                var outputDir = parseResult.GetValue(outputDirOption)!;
                Directory.CreateDirectory(outputDir);
                foreach (var filter in filters)
                {
                    var path = Path.Combine(outputDir, filter.Assembly + ".runsettings");
                    File.WriteAllText(path, FilterEmitter.ToRunSettings(filter));
                    Console.WriteLine(path);
                }
            }
            break;

        default:
            Console.WriteLine(JsonSerializer.Serialize(result, jsonOptions));
            break;
    }

    return exitCode;
});
root.Subcommands.Add(analyzeCommand);

return root.Parse(args).Invoke();
