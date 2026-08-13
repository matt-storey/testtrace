using NUnit.Framework;
using TestTrace.Core;

namespace TestTrace.Core.Tests;

[TestFixture]
public class GlobMatcherTests
{
    [TestCase("SampleApp.Api/appsettings.json", "**/appsettings*.json", true)]
    [TestCase("appsettings.Development.json", "**/appsettings*.json", true)]
    [TestCase("src/Data/Migrations/20240101_Init.cs", "**/Migrations/**", true)]
    [TestCase("Pages/Index.razor", "**/*.razor", true)]
    [TestCase("Views/Home/Index.cshtml", "**/*.cshtml", true)]
    [TestCase("SampleApp.Services/OrderService.cs", "**/appsettings*.json", false)]
    [TestCase("src/appsettingsx/foo.cs", "**/appsettings*.json", false)]
    [TestCase("a/b/c.txt", "a/*/c.txt", true)]
    [TestCase("a/b/d/c.txt", "a/*/c.txt", false)]
    [TestCase("A\\B\\appsettings.json", "**/appsettings*.json", true)]
    public void Matches(string path, string glob, bool expected)
    {
        Assert.That(GlobMatcher.Matches(path, glob), Is.EqualTo(expected));
    }

    [Test]
    public void AnyMatch_ReportsWhichPathAndGlob()
    {
        var matched = GlobMatcher.AnyMatch(
            ["src/A.cs", "SampleApp.Api/appsettings.json"],
            Analyzer.DefaultForceFullRunGlobs,
            out var path, out var glob);

        Assert.Multiple(() =>
        {
            Assert.That(matched, Is.True);
            Assert.That(path, Is.EqualTo("SampleApp.Api/appsettings.json"));
            Assert.That(glob, Is.EqualTo("**/appsettings*.json"));
        });
    }
}
