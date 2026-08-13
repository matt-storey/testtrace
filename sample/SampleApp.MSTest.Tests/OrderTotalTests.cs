using Microsoft.VisualStudio.TestTools.UnitTesting;
using SampleApp.Domain;
using SampleApp.Services;

namespace SampleApp.MSTest.Tests;

/// <summary>
/// Covers the MSTest shapes that differ from the other frameworks: [TestInitialize]
/// as per-test setup, a STATIC [ClassInitialize], and [DataRow]/[DynamicData] whose
/// arguments stay out of the fully qualified name.
/// </summary>
[TestClass]
public class OrderTotalTests
{
    private OrderService _service = null!;
    private Order _order = null!;

    [ClassInitialize]
    public static void ClassInit(TestContext context)
    {
        // Static lifecycle: the detector must not filter statics out.
    }

    [TestInitialize]
    public void Setup()
    {
        _service = new OrderService();
        _order = new Order { Customer = new Customer(1, "  Alan   Turing ", "alan@example.com") };
        _order.Lines.Add(new OrderLine("WIDGET-1", 2, new Money(4.50m, "GBP")));
        _order.Lines.Add(new OrderLine("GADGET-9", 1, new Money(10.00m, "GBP")));
    }

    [TestMethod]
    public void CalculateTotal_SumsLineTotals()
    {
        Assert.AreEqual(new Money(19.00m, "GBP"), _service.CalculateTotal(_order));
    }

    [TestMethod]
    public void DescribeLines_NormalisesSkus()
    {
        CollectionAssert.AreEqual(
            new[] { "2 x WIDGET-1", "1 x GADGET-9" }, _service.DescribeLines(_order).ToArray());
    }

    [TestMethod]
    [DataRow(1, 5.0)]
    [DataRow(3, 15.0)]
    public void CalculateTotal_ScalesWithQuantity(int quantity, double expected)
    {
        var order = new Order { Customer = new Customer(2, "Test", "t@example.com") };
        order.Lines.Add(new OrderLine("A", quantity, new Money(5.00m, "GBP")));

        Assert.AreEqual((decimal)expected, _service.CalculateTotal(order).Amount);
    }

    public static IEnumerable<object[]> LineCounts()
    {
        yield return [1, 2];
        yield return [6, 3];
    }

    [TestMethod]
    [DynamicData(nameof(LineCounts), DynamicDataSourceType.Method)]
    public async Task EstimateDeliveryDays_DependsOnLineCount(int lineCount, int expectedDays)
    {
        var order = new Order { Customer = new Customer(3, "Bulk", "b@example.com") };
        for (var i = 0; i < lineCount; i++)
            order.Lines.Add(new OrderLine($"SKU-{i}", 1, new Money(1.00m, "GBP")));

        Assert.AreEqual(expectedDays, await _service.EstimateDeliveryDaysAsync(order));
    }
}
