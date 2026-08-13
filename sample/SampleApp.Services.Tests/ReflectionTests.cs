using NUnit.Framework;
using SampleApp.Reflection;

namespace SampleApp.Services.Tests;

[TestFixture]
public class ReflectionTests
{
    [Test]
    public void Describe_TrimsAndTags()
    {
        Assert.That(InvokesByReflection.Describe("  widget "), Is.EqualTo("described:widget"));
    }
}
