using NUnit.Framework;
using SampleApp.Domain;
using SampleApp.Services;

namespace SampleApp.Services.Tests;

[TestFixture]
public class OrderServiceTests
{
    private OrderService _service = null!;
    private Order _order = null!;

    [SetUp]
    public void SetUp()
    {
        _service = new OrderService();
        _order = new Order
        {
            Customer = new Customer(1, "  Ada   Lovelace ", "ada@example.com"),
        };
        _order.Lines.Add(new OrderLine("WIDGET-1", 2, new Money(4.50m, "GBP")));
        _order.Lines.Add(new OrderLine("GADGET-9", 1, new Money(10.00m, "GBP")));
    }

    [Test]
    public void CalculateTotal_SumsLineTotals()
    {
        Assert.That(_service.CalculateTotal(_order), Is.EqualTo(new Money(19.00m, "GBP")));
    }

    private static IEnumerable<TestCaseData> TotalCases()
    {
        yield return new TestCaseData(new[] { ("A", 1, 5.00m) }, 5.00m);
        yield return new TestCaseData(new[] { ("A", 2, 2.50m), ("B", 1, 1.00m) }, 6.00m);
    }

    [TestCaseSource(nameof(TotalCases))]
    public void CalculateTotal_FromCases((string Sku, int Quantity, decimal UnitPrice)[] lines, decimal expected)
    {
        var order = new Order { Customer = new Customer(2, "Test", "t@example.com") };
        foreach (var (sku, quantity, unitPrice) in lines)
            order.Lines.Add(new OrderLine(sku, quantity, new Money(unitPrice, "GBP")));

        Assert.That(_service.CalculateTotal(order).Amount, Is.EqualTo(expected));
    }

    [Test]
    public void DescribeLines_NormalisesSkus()
    {
        var lines = _service.DescribeLines(_order);
        Assert.That(lines, Is.EqualTo(new[] { "2 x WIDGET-1", "1 x GADGET-9" }));
    }

    [Test]
    public async Task PlaceAsync_ReturnsReceiptWithTotal()
    {
        var receipt = await _service.PlaceAsync(_order);
        Assert.Multiple(() =>
        {
            Assert.That(receipt.OrderId, Is.EqualTo(_order.Id));
            Assert.That(receipt.Total, Is.EqualTo(new Money(19.00m, "GBP")));
            Assert.That(receipt.Summary, Does.Contain("Ada Lovelace"));
        });
    }

    [Test]
    public void PlaceAsync_EmptyOrder_Throws()
    {
        var empty = new Order { Customer = new Customer(3, "Empty", "e@example.com") };
        Assert.ThrowsAsync<InvalidOperationException>(() => _service.PlaceAsync(empty));
    }

    [Test]
    public async Task EstimateDeliveryDaysAsync_SmallOrder_TwoDays()
    {
        Assert.That(await _service.EstimateDeliveryDaysAsync(_order), Is.EqualTo(2));
    }
}
