using SampleApp.Domain;

namespace SampleApp.Services;

public record OrderReceipt(Guid OrderId, Money Total, string Summary);

public interface IOrderService
{
    Money CalculateTotal(Order order);
    IReadOnlyList<string> DescribeLines(Order order);
    Task<OrderReceipt> PlaceAsync(Order order);
    Task<int> EstimateDeliveryDaysAsync(Order order);
}
