using NUnit.Framework;
using TestTrace.Core;

namespace TestTrace.Core.Tests;

[TestFixture]
public class SolutionReaderTests
{
    private string _root = null!;

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), $"testtrace-sln-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_root, recursive: true);

    private void Project(string name, string? assemblyName = null, params string[] tfmsWithOutput)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var assemblyElement = assemblyName is null ? "" : $"<AssemblyName>{assemblyName}</AssemblyName>";
        File.WriteAllText(Path.Combine(dir, $"{name}.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>{assemblyElement}</PropertyGroup></Project>");

        foreach (var tfm in tfmsWithOutput)
        {
            var bin = Path.Combine(dir, "bin", "Release", tfm);
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, $"{assemblyName ?? name}.dll"), "not a real assembly");
        }
    }

    private string WriteSlnx(params string[] names)
    {
        var path = Path.Combine(_root, "Test.slnx");
        var entries = names.Select(n => $"  <Project Path=\"{n}/{n}.csproj\" />");
        File.WriteAllText(path, $"<Solution>\n{string.Join("\n", entries)}\n</Solution>");
        return path;
    }

    private string WriteSln(params string[] names)
    {
        var path = Path.Combine(_root, "Test.sln");
        var entries = names.Select(n =>
            $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{n}\", \"{n}\\{n}.csproj\", \"{{{Guid.NewGuid()}}}\"\nEndProject");
        File.WriteAllText(path, string.Join("\n", entries));
        return path;
    }

    [Test]
    public void Slnx_ReadsProjectsAndResolvesOutputs()
    {
        Project("Alpha", null, "net8.0");
        Project("Beta", null, "net8.0");
        var load = SolutionReader.Load(WriteSlnx("Alpha", "Beta"), "Release", "net8.0");

        Assert.Multiple(() =>
        {
            Assert.That(load.Projects.Select(p => p.AssemblyName), Is.EquivalentTo(new[] { "Alpha", "Beta" }));
            Assert.That(load.OutputDirectories, Has.Count.EqualTo(2));
            Assert.That(load.Warnings, Is.Empty);
        });
    }

    [Test]
    public void ClassicSln_ReadsBackslashPathsAndSkipsSolutionFolders()
    {
        Project("Alpha", null, "net8.0");
        var path = WriteSln("Alpha");
        // Solution folders use a different type GUID and a non-project path.
        File.AppendAllText(path,
            "\nProject(\"{2150E333-8FDC-42A3-9474-1A3956D46DE8}\") = \"Solution Items\", \"Solution Items\", \"{ABC}\"\nEndProject\n");

        var load = SolutionReader.Load(path, "Release", "net8.0");

        Assert.That(load.Projects.Select(p => p.AssemblyName), Is.EqualTo(new[] { "Alpha" }));
    }

    [Test]
    public void AssemblyName_OverrideIsHonoured()
    {
        Project("Alpha", "Custom.Name", "net8.0");
        var load = SolutionReader.Load(WriteSlnx("Alpha"), "Release", "net8.0");

        Assert.That(load.Projects.Single().AssemblyName, Is.EqualTo("Custom.Name"));
    }

    [Test]
    public void MultiTargeted_PicksOneFrameworkForTheWholeSolution()
    {
        // Alpha offers both; Beta only net8.0. Choosing per-project would mix TFMs.
        Project("Alpha", null, "net8.0", "net10.0");
        Project("Beta", null, "net8.0");

        var load = SolutionReader.Load(WriteSlnx("Alpha", "Beta"), "Release", null);

        Assert.Multiple(() =>
        {
            Assert.That(load.OutputDirectories.Select(Path.GetFileName),
                Has.All.EqualTo("net8.0"), "every project must resolve to the same TFM");
            Assert.That(load.Warnings, Has.Some.Contains("multiple frameworks"));
        });
    }

    [Test]
    public void DisjointSingleTargets_AreReported_NotSilentlyMixed()
    {
        // No framework is common to both. Spanning them is legitimate (baseline and
        // current resolve identically), but it must be visible.
        Project("Alpha", null, "net10.0");
        Project("Beta", null, "net8.0");

        var load = SolutionReader.Load(WriteSlnx("Alpha", "Beta"), "Release", null);

        Assert.Multiple(() =>
        {
            Assert.That(load.OutputDirectories, Has.Count.EqualTo(2), "both projects still analysed");
            Assert.That(load.Warnings, Has.Some.Contains("span"),
                "spanning multiple frameworks must be reported");
        });
    }

    [Test]
    public void DisjointTargets_DoNotDropAMultiTargetedProject()
    {
        // Regression: with no common framework, Gamma matched nothing and was
        // dropped from the analysis entirely despite being built.
        Project("Alpha", null, "net10.0");
        Project("Beta", null, "net8.0");
        Project("Gamma", null, "net8.0", "net10.0");

        var load = SolutionReader.Load(WriteSlnx("Alpha", "Beta", "Gamma"), "Release", null);

        Assert.That(load.Projects.Single(p => p.AssemblyName == "Gamma").OutputDirectory,
            Is.Not.Null, "a built project must never be dropped");
    }

    [Test]
    public void ExplicitFramework_PinsEveryProject()
    {
        Project("Alpha", null, "net8.0", "net10.0");
        Project("Beta", null, "net8.0", "net10.0");

        var load = SolutionReader.Load(WriteSlnx("Alpha", "Beta"), "Release", "net8.0");

        Assert.Multiple(() =>
        {
            Assert.That(load.OutputDirectories.Select(Path.GetFileName), Has.All.EqualTo("net8.0"));
            Assert.That(load.Warnings, Is.Empty);
        });
    }

    [Test]
    public void UnbuiltProject_WarnsAndIsSkipped()
    {
        Project("Alpha", null, "net8.0");
        Project("Beta"); // never built

        var load = SolutionReader.Load(WriteSlnx("Alpha", "Beta"), "Release", "net8.0");

        Assert.Multiple(() =>
        {
            Assert.That(load.OutputDirectories, Has.Count.EqualTo(1));
            Assert.That(load.Warnings, Has.Some.Contains("Beta"));
            Assert.That(load.ToScope().Include, Is.EquivalentTo(new[] { "Alpha", "Beta" }),
                "scope still covers unbuilt projects");
        });
    }

    [Test]
    public void ToScope_ContainsSolutionProjectsOnly()
    {
        Project("Alpha", null, "net8.0");
        var scope = SolutionReader.Load(WriteSlnx("Alpha"), "Release", "net8.0").ToScope();

        Assert.Multiple(() =>
        {
            Assert.That(scope.IsInScope("Alpha", "irrelevant.dll"), Is.True);
            Assert.That(scope.IsInScope("Newtonsoft.Json", "irrelevant.dll"), Is.False);
        });
    }

    private void AddProjectReference(string from, string to)
    {
        var path = Path.Combine(_root, from, $"{from}.csproj");
        File.WriteAllText(path,
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><ProjectReference Include="..\{to}\{to}.csproj" /></ItemGroup>
            </Project>
            """);
    }

    [Test]
    public void LoadProjects_PullsInProjectReferencesTransitively()
    {
        // Tests -> Services -> Common. Naming only the test project must still put
        // the code under test in scope, or every change to it would be untraceable.
        Project("Tests", null, "net8.0");
        Project("Services", null, "net8.0");
        Project("Common", null, "net8.0");
        AddProjectReference("Tests", "Services");
        AddProjectReference("Services", "Common");

        var load = SolutionReader.LoadProjects(
            [Path.Combine(_root, "Tests", "Tests.csproj")], "Release", "net8.0");

        Assert.That(load.ToScope().Include, Is.EquivalentTo(new[] { "Tests", "Services", "Common" }));
    }

    [Test]
    public void LoadProjects_SurvivesCircularReferences()
    {
        Project("A", null, "net8.0");
        Project("B", null, "net8.0");
        AddProjectReference("A", "B");
        AddProjectReference("B", "A");

        var load = SolutionReader.LoadProjects([Path.Combine(_root, "A", "A.csproj")], "Release", "net8.0");

        Assert.That(load.ToScope().Include, Is.EquivalentTo(new[] { "A", "B" }));
    }

    [Test]
    public void LoadProjects_MissingProject_Throws() =>
        Assert.Throws<FileNotFoundException>(() =>
            SolutionReader.LoadProjects([Path.Combine(_root, "None.csproj")], "Release", null));

    [Test]
    public void MissingSolution_Throws() =>
        Assert.Throws<FileNotFoundException>(() =>
            SolutionReader.Load(Path.Combine(_root, "None.slnx"), "Release", null));

    [Test]
    public void IsSolution_RecognisesBothFormats()
    {
        Assert.Multiple(() =>
        {
            Assert.That(SolutionReader.IsSolution("a/b.sln"), Is.True);
            Assert.That(SolutionReader.IsSolution("a/b.slnx"), Is.True);
            Assert.That(SolutionReader.IsSolution("a/b.csproj"), Is.False);
        });
    }
}
