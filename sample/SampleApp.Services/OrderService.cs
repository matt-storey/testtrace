using SampleApp.Common;
using SampleApp.Domain;

namespace SampleApp.Services;

public class OrderService : IOrderService
{
    public Money CalculateTotal(Order order)
    {
        var total = Money.Zero("GBP");
        foreach (var line in order.Lines)
            total += line.UnitPrice.Times(line.Quantity);
        return total;
    }

    public IReadOnlyList<string> DescribeLines(Order order) =>
        order.Lines
            .Select(line => $"{line.Quantity} x {StringUtils.Normalise(line.Sku)}")
            .ToList();

    public async Task<OrderReceipt> PlaceAsync(Order order)
    {
        if (order.Lines.Count == 0)
            throw new InvalidOperationException("An order needs at least one line.");
        await Task.Yield();
        var total = CalculateTotal(order);
        var summary = $"{order.Lines.Count} line(s) for {StringUtils.Normalise(order.Customer.Name)}";
        return new OrderReceipt(order.Id, total, summary);
    }

    public async Task<int> EstimateDeliveryDaysAsync(Order order)
    {
        await Task.Yield();
        var days = 2;
        if (order.Lines.Count > 5)
            days += 1;
        return days;
    }
}
