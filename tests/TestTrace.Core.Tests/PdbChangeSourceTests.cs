using System.Runtime.CompilerServices;
using NUnit.Framework;
using TestTrace.Core;

namespace TestTrace.Core.Tests;

[TestFixture]
public class PdbChangeSourceTests
{
    private static (string File, int Line) Here([CallerFilePath] string file = "", [CallerLineNumber] int line = 0) =>
        (file, line);

    [Test]
    public void ChangedFiles_ParseForms()
    {
        var ranges = ChangedFiles.Parse(["a/b.cs", "a/b.cs:12", "a/b.cs:12-34", "", "# comment"]);

        Assert.That(ranges, Is.EqualTo(new[]
        {
            new ChangedFileRange("a/b.cs", 1, int.MaxValue),
            new ChangedFileRange("a/b.cs", 12, 12),
            new ChangedFileRange("a/b.cs", 12, 34),
        }));
    }

    [Test]
    public void UnifiedDiff_IsDetectedAndParsedIntoRanges()
    {
        var diff = """
            diff --git a/src/A.cs b/src/A.cs
            index 1111111..2222222 100644
            --- a/src/A.cs
            +++ b/src/A.cs
            @@ -10,0 +11,3 @@ public class A
            +    added one
            +    added two
            +    added three
            diff --git a/src/B.cs b/src/B.cs
            --- a/src/B.cs
            +++ b/src/B.cs
            @@ -5 +5 @@
            -old
            +new
            """.Split('\n');

        Assert.That(ChangedFiles.LooksLikeUnifiedDiff(diff), Is.True);
        // Paths are kept verbatim, prefix included; matching absorbs the prefix, so
        // no producer's convention is baked into the parser.
        Assert.That(ChangedFiles.Parse(diff), Is.EqualTo(new[]
        {
            new ChangedFileRange("b/src/A.cs", 11, 13),
            new ChangedFileRange("b/src/B.cs", 5, 5),
        }));
    }

    [Test]
    public void UnifiedDiff_DetectedWithoutAnyToolSpecificHeader()
    {
        // Plain `diff -u` output: no vendor header line, no path prefix, and a
        // tab-separated timestamp column.
        var diff = new[]
        {
            "--- old.cs\t2026-08-14 10:43:07",
            "+++ new.cs\t2026-08-14 10:43:07",
            "@@ -1,4 +1,4 @@",
            " line1",
            "-line2",
            "+CHANGED",
        };

        Assert.That(ChangedFiles.LooksLikeUnifiedDiff(diff), Is.True);
        Assert.That(ChangedFiles.Parse(diff), Is.EqualTo(new[] { new ChangedFileRange("new.cs", 1, 4) }));
    }

    [Test]
    public void PrefixedDiffPath_StillMatchesItsSourceDocument()
    {
        // The prefix segment a diff producer adds must not stop the path matching
        // its real source document.
        var (file, line) = Here();
        var prefixed = "b/" + Path.GetFileName(file);

        var result = PdbChangeSource.GetChangedMethods(
            AppContext.BaseDirectory, [new ChangedFileRange(prefixed, line, line)]);

        Assert.Multiple(() =>
        {
            Assert.That(result.UnanalyzableFile, Is.Null);
            Assert.That(result.Changes.Select(c => c.Fqn),
                Has.Some.Contains(nameof(PrefixedDiffPath_StillMatchesItsSourceDocument)));
        });
    }

    [Test]
    public void UnifiedDiff_PureDeletionRecordsTheAdjacentLine()
    {
        var diff = """
            +++ b/src/A.cs
            @@ -10,3 +9,0 @@
            -gone one
            -gone two
            -gone three
            """.Split('\n');

        // A deletion still affects the surrounding method, so it must not vanish.
        Assert.That(ChangedFiles.Parse(diff), Is.EqualTo(new[] { new ChangedFileRange("b/src/A.cs", 9, 9) }));
    }

    [Test]
    public void UnifiedDiff_DeletedFileIsIgnored()
    {
        var diff = """
            +++ /dev/null
            @@ -1,5 +0,0 @@
            """.Split('\n');

        Assert.That(ChangedFiles.Parse(diff), Is.Empty);
    }

    [Test]
    public void PlainList_IsStillAccepted()
    {
        var lines = new[] { "src/A.cs", "src/B.cs:12-34" };

        Assert.That(ChangedFiles.LooksLikeUnifiedDiff(lines), Is.False);
        Assert.That(ChangedFiles.Parse(lines), Is.EqualTo(new[]
        {
            new ChangedFileRange("src/A.cs", 1, int.MaxValue),
            new ChangedFileRange("src/B.cs", 12, 34),
        }));
    }

    [Test]
    public void LineRange_IntersectsExactlyThisTestMethod()
    {
        var (file, line) = Here();
        var result = PdbChangeSource.GetChangedMethods(
            AppContext.BaseDirectory,
            [new ChangedFileRange(file, line, line)]);

        Assert.Multiple(() =>
        {
            Assert.That(result.PdbCount, Is.GreaterThan(0));
            Assert.That(result.UnanalyzableFile, Is.Null);
            Assert.That(result.Changes.Select(c => c.Fqn),
                Has.Some.Contains("LineRange_IntersectsExactlyThisTestMethod"));
            Assert.That(result.Changes.Select(c => c.Fqn),
                Has.None.Contains("ChangedFiles_ParseForms"),
                "a single-line range must not select sibling methods");
        });
    }

    [Test]
    public void WholeFile_AttributesGeneratedMembersToOwners()
    {
        // MethodHasherTests.cs contains HasherFixture: lambdas, async and iterator
        // state machines, local functions. Whole-file intersection must fold them
        // all back onto user methods.
        var (file, _) = Here();
        var fixtureFile = Path.Combine(Path.GetDirectoryName(file)!, "MethodHasherTests.cs");
        var result = PdbChangeSource.GetChangedMethods(
            AppContext.BaseDirectory,
            [new ChangedFileRange(fixtureFile, 1, int.MaxValue)]);

        var fqns = result.Changes.Select(c => c.Fqn).ToList();
        Assert.Multiple(() =>
        {
            Assert.That(fqns, Has.Some.Contains("HasherFixture::WithAwait"));
            Assert.That(fqns, Has.Some.Contains("HasherFixture::WithLambdas"));
            Assert.That(fqns, Has.None.Matches<string>(f => f.Contains(">b__") || f.Contains(">d__") || f.Contains(">g__")),
                "generated members must be reported as their owners");
        });
    }

    [Test]
    public void NonSourceFile_IsUnanalyzable()
    {
        var result = PdbChangeSource.GetChangedMethods(
            AppContext.BaseDirectory,
            [new ChangedFileRange("SomeProject/SomeProject.csproj", 1, int.MaxValue)]);

        Assert.That(result.UnanalyzableFile, Is.EqualTo("SomeProject/SomeProject.csproj"));
    }
}
