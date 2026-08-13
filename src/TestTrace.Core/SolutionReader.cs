using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TestTrace.Core;

public sealed class SolutionProject
{
    public string ProjectPath { get; init; } = "";

    /// <summary>Assembly simple name: &lt;AssemblyName&gt; if set, else the file name.</summary>
    public string AssemblyName { get; init; } = "";

    /// <summary>Resolved build output directory, or null when none was found.</summary>
    public string? OutputDirectory { get; set; }
}

public sealed class SolutionLoad
{
    public List<SolutionProject> Projects { get; } = [];
    public List<string> Warnings { get; } = [];

    public List<string> OutputDirectories => Projects
        .Where(p => p.OutputDirectory is not null)
        .Select(p => p.OutputDirectory!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.Ordinal)
        .ToList();

    /// <summary>Scope built from the solution itself: the projects it contains are
    /// exactly "your code", a far better signal than PDB adjacency.</summary>
    public AssemblyScope ToScope() => new()
    {
        Include = Projects.Select(p => p.AssemblyName).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList(),
    };
}

/// <summary>
/// Reads `.sln` and `.slnx` solutions and resolves each project's build output
/// directory by convention (bin/&lt;config&gt;/&lt;tfm&gt;, or the .NET 8+ artifacts
/// layout). MSBuild is deliberately not invoked: it would mean either a large package
/// dependency in Core or a subprocess per project. Projects with a custom OutputPath
/// will not resolve — they are reported as warnings, and `--current` remains the
/// explicit escape hatch.
/// </summary>
public static class SolutionReader
{
    private static readonly Regex SlnProjectLine = new(
        @"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""(?<path>[^""]+)""",
        RegexOptions.Multiline | RegexOptions.Compiled);

    public static bool IsSolution(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".sln" or ".slnx";

    public static SolutionLoad Load(string solutionPath, string configuration, string? framework)
    {
        if (!File.Exists(solutionPath))
            throw new FileNotFoundException($"solution not found: {solutionPath}");

        var solutionDir = Path.GetDirectoryName(Path.GetFullPath(solutionPath))!;
        var load = new SolutionLoad();
        var paths = new List<string>();

        foreach (var relative in ReadProjectPaths(solutionPath))
        {
            var projectPath = Path.GetFullPath(Path.Combine(solutionDir, relative.Replace('\\', '/')));
            if (File.Exists(projectPath))
                paths.Add(projectPath);
            else
                load.Warnings.Add($"project listed in the solution does not exist: {relative}");
        }

        if (paths.Count == 0)
            throw new InvalidDataException($"no projects found in {solutionPath}");
        return Resolve(paths, _ => solutionDir, configuration, framework, load);
    }

    /// <summary>
    /// Same resolution for explicitly named project files. The artifacts-layout root
    /// is not known as it is for a solution, so it is discovered per project by
    /// walking up for an `artifacts/bin` directory.
    /// </summary>
    public static SolutionLoad LoadProjects(IReadOnlyList<string> projectPaths, string configuration, string? framework)
    {
        var load = new SolutionLoad();
        var paths = new List<string>();
        foreach (var path in projectPaths)
        {
            var full = Path.GetFullPath(path);
            if (File.Exists(full))
                paths.Add(full);
            else
                load.Warnings.Add($"project not found: {path}");
        }

        if (paths.Count == 0)
            throw new FileNotFoundException($"no project file found among: {string.Join(", ", projectPaths)}");
        return Resolve(paths, ArtifactsRootFor, configuration, framework, load);
    }

    private static string ArtifactsRootFor(string projectPath)
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(projectPath)!);
        for (var depth = 0; directory is not null && depth < 8; depth++, directory = directory.Parent)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "artifacts", "bin")))
                return directory.FullName;
        }

        return Path.GetDirectoryName(projectPath)!;
    }

    private static SolutionLoad Resolve(
        List<string> projectPaths, Func<string, string> rootFor, string configuration, string? framework, SolutionLoad load)
    {
        var projects = new List<SolutionProject>();
        var candidatesByProject = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var projectPath in WithProjectReferences(projectPaths))
        {
            var project = new SolutionProject
            {
                ProjectPath = projectPath,
                AssemblyName = AssemblyNameOf(projectPath),
            };
            projects.Add(project);
            candidatesByProject[projectPath] = OutputCandidates(project, rootFor(projectPath), configuration, framework);
        }

        // Pick ONE target framework for the whole solution. Letting each project
        // choose independently could mix TFMs across a single analysis, which makes
        // every assembly look changed — the one thing the design forbids.
        var chosenTfm = framework ?? ChooseCommonTfm(candidatesByProject.Values, load.Warnings);

        foreach (var project in projects)
        {
            var candidates = candidatesByProject[project.ProjectPath];
            if (candidates.Count == 0)
            {
                load.Warnings.Add(
                    $"no {configuration} build output for {Path.GetFileName(project.ProjectPath)}; " +
                    "build it, or pass --current explicitly");
            }
            else
            {
                // Prefer the solution-wide framework; otherwise this project's own
                // ordinal-first one. A built project is never dropped.
                var match = candidates.FirstOrDefault(d => TfmOf(d).Equals(chosenTfm, StringComparison.OrdinalIgnoreCase));
                if (match is null && chosenTfm is not null)
                {
                    load.Warnings.Add(
                        $"{Path.GetFileName(project.ProjectPath)} has no {chosenTfm} output; " +
                        $"using {TfmOf(candidates[0])} — this analysis spans more than one target framework");
                }

                project.OutputDirectory = match ?? candidates[0];
            }

            load.Projects.Add(project);
        }

        var spanned = load.Projects
            .Where(p => p.OutputDirectory is not null)
            .Select(p => TfmOf(p.OutputDirectory!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (spanned.Count > 1)
        {
            // Not fatal — a solution can legitimately span frameworks, and baseline
            // and current resolve identically so the diff stays apples-to-apples.
            // But it must never be silent.
            load.Warnings.Add(
                $"resolved outputs span {spanned.Count} target frameworks ({string.Join(", ", spanned)}); " +
                "pass --framework to pin one");
        }

        return load;
    }

    /// <summary>
    /// One framework for the whole solution: the ordinal-first framework common to
    /// every built project. When no framework is common to all (genuinely disjoint
    /// targets) the most widely available one wins, and projects lacking it fall back
    /// to their own — reported, never silent.
    /// </summary>
    private static string? ChooseCommonTfm(IEnumerable<List<string>> candidateSets, List<string> warnings)
    {
        var sets = candidateSets
            .Where(c => c.Count > 0)
            .Select(c => c.Select(TfmOf).ToHashSet(StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (sets.Count == 0)
            return null;

        var common = sets.Skip(1).Aggregate(new HashSet<string>(sets[0], StringComparer.OrdinalIgnoreCase),
            (acc, s) => { acc.IntersectWith(s); return acc; });

        string? chosen;
        if (common.Count > 0)
        {
            chosen = common.Order(StringComparer.OrdinalIgnoreCase).First();
        }
        else
        {
            chosen = sets.SelectMany(s => s)
                .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .First().Key;
        }

        if (sets.Any(s => s.Count > 1) || common.Count == 0)
        {
            var all = sets.SelectMany(s => s).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal);
            warnings.Add(
                $"projects target multiple frameworks ({string.Join(", ", all)}); " +
                $"using '{chosen}' where available — pass --framework to choose");
        }

        return chosen;
    }

    private static string TfmOf(string outputDirectory) => Path.GetFileName(outputDirectory);

    public static IEnumerable<string> ReadProjectPaths(string solutionPath)
    {
        var text = File.ReadAllText(solutionPath);
        if (Path.GetExtension(solutionPath).Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            // <Solution><Folder><Project Path="..."/></Folder></Solution> — Project
            // elements may sit at any depth.
            return XDocument.Parse(text)
                .Descendants("Project")
                .Select(e => (string?)e.Attribute("Path"))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => p!)
                .ToList();
        }

        return SlnProjectLine.Matches(text)
            .Select(m => m.Groups["path"].Value)
            .Where(p => Path.GetExtension(p).EndsWith("proj", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    /// <summary>
    /// Expand a project list with everything it references, transitively.
    ///
    /// Without this, `--project MyTests.csproj` would put only the test assembly in
    /// scope, so any change to the code under test would be untraceable and escalate
    /// to RUN_EVERYTHING — the mode would be useless. A project's references are as
    /// much "your code" as the project itself.
    /// </summary>
    private static List<string> WithProjectReferences(IEnumerable<string> projectPaths)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(projectPaths);

        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            if (!seen.Add(path))
                continue;
            ordered.Add(path);

            foreach (var reference in ProjectReferencesOf(path))
            {
                if (!seen.Contains(reference))
                    queue.Enqueue(reference);
            }
        }

        return ordered;
    }

    private static IEnumerable<string> ProjectReferencesOf(string projectPath)
    {
        List<string> references = [];
        try
        {
            var directory = Path.GetDirectoryName(projectPath)!;
            references = XDocument.Load(projectPath)
                .Descendants("ProjectReference")
                .Select(e => (string?)e.Attribute("Include"))
                .Where(i => !string.IsNullOrWhiteSpace(i))
                .Select(i => Path.GetFullPath(Path.Combine(directory, i!.Replace('\\', '/'))))
                .Where(File.Exists)
                .ToList();
        }
        catch (Exception)
        {
            // Unreadable project file: the caller still gets the projects it named.
        }

        return references;
    }

    private static string AssemblyNameOf(string projectPath)
    {
        try
        {
            var explicitName = XDocument.Load(projectPath)
                .Descendants("AssemblyName")
                .Select(e => e.Value.Trim())
                .FirstOrDefault(v => v.Length > 0 && !v.Contains('$'));
            if (explicitName is not null)
                return explicitName;
        }
        catch (Exception)
        {
            // Unreadable project file: fall back to the file name, which is the
            // default AssemblyName anyway.
        }

        return Path.GetFileNameWithoutExtension(projectPath);
    }

    private static List<string> OutputCandidates(
        SolutionProject project, string solutionDir, string configuration, string? framework)
    {
        var projectDir = Path.GetDirectoryName(project.ProjectPath)!;
        var candidates = new List<string>();

        // Conventional layout: <project>/bin/<Configuration>/<tfm>
        var bin = Path.Combine(projectDir, "bin", configuration);
        if (Directory.Exists(bin))
            candidates.AddRange(TfmDirectories(bin, framework));

        // .NET 8+ artifacts layout: <solution>/artifacts/bin/<project>/<config>[_<tfm>]
        var artifacts = Path.Combine(solutionDir, "artifacts", "bin", Path.GetFileNameWithoutExtension(project.ProjectPath));
        if (Directory.Exists(artifacts))
        {
            candidates.AddRange(Directory.EnumerateDirectories(artifacts)
                .Where(d => Path.GetFileName(d).StartsWith(configuration, StringComparison.OrdinalIgnoreCase))
                .Where(d => framework is null || Path.GetFileName(d).Contains(framework, StringComparison.OrdinalIgnoreCase)));
        }

        return candidates
            .Where(d => Directory.EnumerateFiles(d, "*.dll").Any())
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> TfmDirectories(string binConfigDir, string? framework)
    {
        if (framework is not null)
        {
            var exact = Path.Combine(binConfigDir, framework);
            return Directory.Exists(exact) ? [exact] : [];
        }

        // SDK projects always append the TFM; fall back to the config dir itself for
        // hand-rolled layouts that do not.
        var tfmDirs = Directory.EnumerateDirectories(binConfigDir).ToList();
        return tfmDirs.Count > 0 ? tfmDirs : [binConfigDir];
    }
}
