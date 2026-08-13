using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleApp.Domain;
using SampleApp.Services;

namespace SampleApp.MSTest.Tests;

/// <summary>
/// A second class, deliberately unrelated to OrderTotalTests and to AssemblyHooks:
/// nothing here references the assembly hook, so it is only selected when the
/// assembly-wide scope is honoured.
/// </summary>
[TestClass]
public class DeliveryEstimateTests
{
    [TestMethod]
    public async Task SmallOrder_TakesTwoDays()
    {
        var order = new Order { Customer = new Customer(8, "Solo", "s@example.com") };
        order.Lines.Add(new OrderLine("ONE", 1, new Money(1.00m, "GBP")));

        Assert.AreEqual(2, await new OrderService().EstimateDeliveryDaysAsync(order));
    }
}
