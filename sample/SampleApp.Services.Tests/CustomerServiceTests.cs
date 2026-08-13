using NUnit.Framework;
using SampleApp.Domain;
using SampleApp.Services;

namespace SampleApp.Services.Tests;

[TestFixture]
public class CustomerServiceTests
{
    private readonly CustomerService _service = new();

    [Test]
    public void GetDisplayName_NormalisesWhitespace()
    {
        var customer = new Customer(1, "  Grace   Hopper ", "grace@example.com");
        Assert.That(_service.GetDisplayName(customer), Is.EqualTo("Grace Hopper"));
    }

    [Test]
    public void GetDisplayName_BlankName_FallsBackToId()
    {
        var customer = new Customer(42, "   ", "someone@example.com");
        Assert.That(_service.GetDisplayName(customer), Is.EqualTo("Customer #42"));
    }

    [Test]
    public void IsContactable_RequiresAtSign()
    {
        Assert.Multiple(() =>
        {
            Assert.That(_service.IsContactable(new Customer(1, "A", "a@example.com")), Is.True);
            Assert.That(_service.IsContactable(new Customer(2, "B", "not-an-email")), Is.False);
        });
    }
}
