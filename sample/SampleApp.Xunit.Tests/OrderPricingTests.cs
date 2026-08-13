using SampleApp.Domain;
using SampleApp.Services;
using Xunit;

namespace SampleApp.Xunit.Tests;

/// <summary>
/// Covers the xUnit shapes that differ structurally from NUnit: the constructor as
/// per-test setup, [Theory] with inline and member data, and Dispose as teardown.
/// </summary>
public class OrderPricingTests : IDisposable
{
    private readonly OrderService _service;
    private readonly Order _order;

    // xUnit builds a fresh instance per test, so this constructor IS the setup.
    public OrderPricingTests()
    {
        _service = new OrderService();
        _order = new Order
        {
            Customer = new Customer(1, "  Grace   Hopper ", "grace@example.com"),
        };
        _order.Lines.Add(new OrderLine("WIDGET-1", 2, new Money(4.50m, "GBP")));
        _order.Lines.Add(new OrderLine("GADGET-9", 1, new Money(10.00m, "GBP")));
    }

    public void Dispose() => GC.SuppressFinalize(this);

    [Fact]
    public void CalculateTotal_SumsLineTotals()
    {
        Assert.Equal(new Money(19.00m, "GBP"), _service.CalculateTotal(_order));
    }

    [Fact]
    public void DescribeLines_NormalisesSkus()
    {
        Assert.Equal(new[] { "2 x WIDGET-1", "1 x GADGET-9" }, _service.DescribeLines(_order));
    }

    [Theory]
    [InlineData(1, 5.00)]
    [InlineData(3, 15.00)]
    public void CalculateTotal_ScalesWithQuantity(int quantity, double expected)
    {
        var order = new Order { Customer = new Customer(2, "Test", "t@example.com") };
        order.Lines.Add(new OrderLine("A", quantity, new Money(5.00m, "GBP")));

        Assert.Equal((decimal)expected, _service.CalculateTotal(order).Amount);
    }

    public static IEnumerable<object[]> DiscountCases()
    {
        yield return [1, 2];
        yield return [6, 3];
    }

    [Theory]
    [MemberData(nameof(DiscountCases))]
    public async Task EstimateDeliveryDays_DependsOnLineCount(int lineCount, int expectedDays)
    {
        var order = new Order { Customer = new Customer(3, "Bulk", "b@example.com") };
        for (var i = 0; i < lineCount; i++)
            order.Lines.Add(new OrderLine($"SKU-{i}", 1, new Money(1.00m, "GBP")));

        Assert.Equal(expectedDays, await _service.EstimateDeliveryDaysAsync(order));
    }

    [Fact]
    public async Task PlaceAsync_EmptyOrder_Throws()
    {
        var empty = new Order { Customer = new Customer(4, "Empty", "e@example.com") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.PlaceAsync(empty));
    }
}
