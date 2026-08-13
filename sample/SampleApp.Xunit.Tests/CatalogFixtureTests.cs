using SampleApp.Domain;
using SampleApp.Services;
using Xunit;

namespace SampleApp.Xunit.Tests;

/// <summary>
/// Shared state built by the runner, not by any call site in this assembly. xUnit
/// instantiates CatalogFixture reflectively and injects it, so nothing statically
/// calls its constructor — the graph needs the IClassFixture edge to connect a change
/// in there to these tests.
/// </summary>
public sealed class CatalogFixture
{
    public IReadOnlyList<OrderLine> StandardLines { get; }

    public CatalogFixture()
    {
        StandardLines =
        [
            new OrderLine("BOOK-1", 1, new Money(12.00m, "GBP")),
            new OrderLine("BOOK-2", 2, new Money(3.00m, "GBP")),
        ];
    }
}

public class CatalogFixtureTests : IClassFixture<CatalogFixture>
{
    private readonly CatalogFixture _catalog;
    private readonly OrderService _service = new();

    public CatalogFixtureTests(CatalogFixture catalog) => _catalog = catalog;

    private Order BuildOrder()
    {
        var order = new Order { Customer = new Customer(7, "Catalog", "c@example.com") };
        foreach (var line in _catalog.StandardLines)
            order.Lines.Add(line);
        return order;
    }

    [Fact]
    public void CatalogOrder_TotalsToExpectedAmount()
    {
        Assert.Equal(new Money(18.00m, "GBP"), _service.CalculateTotal(BuildOrder()));
    }

    [Fact]
    public void CatalogOrder_DescribesEveryLine()
    {
        Assert.Equal(2, _service.DescribeLines(BuildOrder()).Count);
    }
}
