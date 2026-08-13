using SampleApp.Domain;
using SampleApp.Services;
using TUnit.Core;

namespace SampleApp.TUnit.Tests;

public class PricingTests
{
    [Test]
    public async Task CalculateTotal_SumsLines()
    {
        var order = new Order { Customer = new Customer(6, "Grace", "g@example.com") };
        order.Lines.Add(new OrderLine("A", 2, new Money(3.00m, "GBP")));
        order.Lines.Add(new OrderLine("B", 1, new Money(4.00m, "GBP")));

        var service = new OrderService();
        await Assert.That(service.CalculateTotal(order)).IsEqualTo(new Money(10.00m, "GBP"));
    }
}
