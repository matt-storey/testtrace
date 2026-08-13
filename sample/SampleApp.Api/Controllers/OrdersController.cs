using Microsoft.AspNetCore.Mvc;
using SampleApp.Domain;
using SampleApp.Services;

namespace SampleApp.Api.Controllers;

public record OrderRequest(int CustomerId, string CustomerName, string CustomerEmail, List<OrderLineRequest> Lines);

public record OrderLineRequest(string Sku, int Quantity, decimal UnitPrice);

[ApiController]
[Route("orders")]
public class OrdersController : ControllerBase
{
    private readonly IOrderService _orders;

    public OrdersController(IOrderService orders) => _orders = orders;

    [HttpPost]
    public async Task<ActionResult<OrderReceipt>> Place(OrderRequest request)
    {
        var receipt = await _orders.PlaceAsync(ToOrder(request));
        return Ok(receipt);
    }

    [HttpPost("total")]
    public ActionResult<decimal> Total(OrderRequest request)
    {
        var total = _orders.CalculateTotal(ToOrder(request));
        return Ok(total.Amount);
    }

    [HttpPost("estimate")]
    public async Task<ActionResult<int>> Estimate(OrderRequest request) =>
        Ok(await _orders.EstimateDeliveryDaysAsync(ToOrder(request)));

    private static Order ToOrder(OrderRequest request)
    {
        var order = new Order
        {
            Customer = new Customer(request.CustomerId, request.CustomerName, request.CustomerEmail),
        };
        order.Lines.AddRange(request.Lines.Select(l => new OrderLine(l.Sku, l.Quantity, new Money(l.UnitPrice, "GBP"))));
        return order;
    }
}
