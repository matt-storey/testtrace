namespace SampleApp.Domain;

public record OrderLine(string Sku, int Quantity, Money UnitPrice);

public class Order
{
    public Guid Id { get; } = Guid.NewGuid();
    public required Customer Customer { get; init; }
    public List<OrderLine> Lines { get; } = [];
}
