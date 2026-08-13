using System.Diagnostics;
using System.Text;

namespace TestTrace.Scenarios.Tests;

/// <summary>
/// A disposable copy of /sample that can be built, mutated and rebuilt.
///
/// Baseline and current are produced from the SAME directory on purpose:
/// deterministic MVIDs incorporate source paths, so building the two states in
/// different directories would make every assembly look changed.
/// </summary>
public sealed class SampleWorkspace : IDisposable
{
    private static readonly string[] TestProjects =
    [
        "SampleApp.Domain.Tests", "SampleApp.Services.Tests", "SampleApp.Api.Tests",
        "SampleApp.Xunit.Tests", "SampleApp.TUnit.Tests", "SampleApp.MSTest.Tests",
    ];

    public string Root { get; }
    public string Tfm { get; }

    /// <summary>Pristine text of every source file, so a scenario's edits can be
    /// undone without recopying the tree.</summary>
    private readonly Dictionary<string, string> _pristine = new(StringComparer.Ordinal);

    private SampleWorkspace(string root, string tfm)
    {
        Root = root;
        Tfm = tfm;
    }

    /// <summary>Revert every source file edited since the workspace was created.</summary>
    public void RestoreSources()
    {
        foreach (var (path, text) in _pristine)
        {
            if (File.ReadAllText(path) != text)
                File.WriteAllText(path, text);
        }
    }

    public static string RepoRoot { get; } = FindRepoRoot();

    public static SampleWorkspace Create(string tfm)
    {
        // Under the repo's gitignored artifacts/ rather than the system temp dir: on
        // macOS Path.GetTempPath() returns /var/folders/... whose /var is a symlink to
        // /private/var, and NuGet restore fails seeing the same directory by two paths
        // ("project.assets.json already exists").
        var root = Path.Combine(RepoRoot, "artifacts", "scenario-work", tfm, Guid.NewGuid().ToString("N")[..8]);
        var workspace = new SampleWorkspace(root, tfm);
        workspace.CopySample();
        return workspace;
    }

    private void CopySample()
    {
        var source = Path.Combine(RepoRoot, "sample");
        Directory.CreateDirectory(Root);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            if (IsBuildArtifact(directory))
                continue;
            Directory.CreateDirectory(Path.Combine(Root, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (IsBuildArtifact(file))
                continue;
            var destination = Path.Combine(Root, Path.GetRelativePath(source, file));
            File.Copy(file, destination, overwrite: true);
            if (Path.GetExtension(destination) is ".cs" or ".json" or ".csproj" or ".props" or ".slnx")
                _pristine[destination] = File.ReadAllText(destination);
        }
    }

    private static bool IsBuildArtifact(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || path.EndsWith($"{Path.DirectorySeparatorChar}bin", StringComparison.Ordinal)
        || path.EndsWith($"{Path.DirectorySeparatorChar}obj", StringComparison.Ordinal);

    /// <summary>
    /// Always a clean build. MSBuild's mtime-based up-to-date check can tie against
    /// files just rewritten, leaking a previous state's outputs into this one.
    /// </summary>
    public void Build()
    {
        var solution = Path.Combine(Root, "SampleApp.slnx");
        // -nodeReuse:false is required, not tuning: persistent MSBuild worker nodes
        // inherit the redirected stdout handle and outlive the build, so the pipe
        // never reaches EOF and WaitForExit blocks forever on an exited process.
        var (exitCode, output) = Run("dotnet",
            $"build \"{solution}\" -c Release -p:TargetFrameworks={Tfm} " +
            "--no-incremental -nodeReuse:false -v:q --nologo");
        if (exitCode != 0)
            throw new InvalidOperationException($"sample build failed:\n{output}");
    }

    /// <summary>Copy the test projects' outputs to a directory that survives rebuilds.</summary>
    public string Harvest(string name)
    {
        var destination = Path.Combine(Root, ".harvest", name);
        if (Directory.Exists(destination))
            Directory.Delete(destination, recursive: true);
        Directory.CreateDirectory(destination);

        foreach (var project in TestProjects)
        {
            var source = Path.Combine(Root, project, "bin", "Release", Tfm);
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        return destination;
    }

    /// <summary>
    /// Apply a scenario's edits and return the changed regions as a unified diff —
    /// the same shape `git diff` produces, so the analyzer's own parser handles it
    /// and the test exercises the real input path.
    /// </summary>
    public string ApplyEdits(Scenario scenario)
    {
        var diff = new StringBuilder();
        foreach (var group in scenario.Edits.GroupBy(e => e.File))
        {
            var path = Path.Combine(Root, group.Key.Replace('/', Path.DirectorySeparatorChar));
            var text = File.ReadAllText(path);
            var hunks = new List<(int Start, int Count)>();

            foreach (var edit in group)
            {
                var find = Normalize(edit.Find);
                var index = text.IndexOf(find, StringComparison.Ordinal);
                if (index < 0)
                    throw new InvalidOperationException(
                        $"scenario '{scenario.Name}': anchor not found in {edit.File}:\n{find}");
                if (text.IndexOf(find, index + 1, StringComparison.Ordinal) >= 0)
                    throw new InvalidOperationException(
                        $"scenario '{scenario.Name}': anchor is ambiguous in {edit.File}:\n{find}");

                var replacement = Normalize(edit.Replace);
                text = text[..index] + replacement + text[(index + find.Length)..];
                var startLine = text[..index].Count(c => c == '\n') + 1;
                hunks.Add((startLine, replacement.Count(c => c == '\n') + 1));
            }

            File.WriteAllText(path, text);
            diff.Append("diff --git a/").Append(group.Key).Append(" b/").Append(group.Key).Append('\n');
            diff.Append("+++ b/").Append(group.Key).Append('\n');
            foreach (var (start, count) in hunks)
                diff.Append("@@ -").Append(start).Append(",0 +").Append(start).Append(',').Append(count).Append(" @@\n");
        }

        return diff.ToString();
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    public static (int ExitCode, string Output) Run(string fileName, string arguments, string? workingDirectory = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = workingDirectory ?? Path.GetTempPath(),
            },
        };

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // Bounded wait: a build that hangs should fail the test, not the whole run.
        if (!process.WaitForExit(milliseconds: 5 * 60 * 1000))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            throw new TimeoutException($"`{fileName} {arguments}` did not finish within 5 minutes");
        }

        lock (output)
            return (process.ExitCode, output.ToString());
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TestTrace.slnx")))
            directory = directory.Parent;
        return directory?.FullName
               ?? throw new InvalidOperationException("could not locate the repository root (TestTrace.slnx)");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp directory is not worth failing a test run over.
        }
    }
}
