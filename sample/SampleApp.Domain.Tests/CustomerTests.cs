using NUnit.Framework;
using SampleApp.Domain;

namespace SampleApp.Domain.Tests;

[TestFixture]
public class CustomerTests
{
    [Test]
    public void RecordEquality_ByValue()
    {
        var a = new Customer(1, "Ada", "ada@example.com");
        var b = new Customer(1, "Ada", "ada@example.com");
        Assert.That(a, Is.EqualTo(b));
    }

    [Test]
    public void WithExpression_ChangesOnlyNamedProperty()
    {
        var a = new Customer(1, "Ada", "ada@example.com");
        var renamed = a with { Name = "Ada L." };
        Assert.Multiple(() =>
        {
            Assert.That(renamed.Name, Is.EqualTo("Ada L."));
            Assert.That(renamed.Id, Is.EqualTo(a.Id));
            Assert.That(renamed.Email, Is.EqualTo(a.Email));
        });
    }
}
