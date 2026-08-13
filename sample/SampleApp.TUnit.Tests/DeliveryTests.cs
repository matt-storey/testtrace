using SampleApp.Domain;
using SampleApp.Services;
using TUnit.Core;

namespace SampleApp.TUnit.Tests;

/// <summary>
/// Covers the TUnit shapes that differ from the VSTest frameworks: [Before(Test)]
/// hooks instead of a constructor or [SetUp], [Arguments] for inline data, and
/// [MethodDataSource] for generated cases.
/// </summary>
public class DeliveryTests
{
    private OrderService _service = null!;

    [Before(HookType.Test)]
    public void Setup() => _service = new OrderService();

    [Test]
    public async Task SmallOrder_TakesTwoDays()
    {
        var order = OrderWith(1);
        await Assert.That(await _service.EstimateDeliveryDaysAsync(order)).IsEqualTo(2);
    }

    [Test]
    [Arguments(1, 2)]
    [Arguments(6, 3)]
    public async Task DeliveryDays_DependOnLineCount(int lineCount, int expectedDays)
    {
        await Assert.That(await _service.EstimateDeliveryDaysAsync(OrderWith(lineCount)))
            .IsEqualTo(expectedDays);
    }

    public static IEnumerable<int> LineCounts()
    {
        yield return 2;
        yield return 4;
    }

    [Test]
    [MethodDataSource(nameof(LineCounts))]
    public async Task ModestOrders_StillTakeTwoDays(int lineCount)
    {
        await Assert.That(await _service.EstimateDeliveryDaysAsync(OrderWith(lineCount))).IsEqualTo(2);
    }

    private static Order OrderWith(int lineCount)
    {
        var order = new Order { Customer = new Customer(5, "Ada", "ada@example.com") };
        for (var i = 0; i < lineCount; i++)
            order.Lines.Add(new OrderLine($"SKU-{i}", 1, new Money(2.00m, "GBP")));
        return order;
    }
}
