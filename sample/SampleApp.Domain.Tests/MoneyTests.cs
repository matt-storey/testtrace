using NUnit.Framework;
using SampleApp.Domain;

namespace SampleApp.Domain.Tests;

[TestFixture]
public class MoneyTests
{
    [Test]
    public void Add_SameCurrency_SumsAmounts()
    {
        var result = new Money(2.50m, "GBP") + new Money(1.25m, "GBP");
        Assert.That(result, Is.EqualTo(new Money(3.75m, "GBP")));
    }

    [Test]
    public void Add_DifferentCurrency_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = new Money(1m, "GBP") + new Money(1m, "USD");
        });
    }

    [Test]
    public void Times_MultipliesAmount()
    {
        Assert.That(new Money(3m, "GBP").Times(4), Is.EqualTo(new Money(12m, "GBP")));
    }
}
