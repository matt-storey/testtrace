using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;

namespace SampleApp.Api.Tests;

[TestFixture]
public class OrdersControllerTests
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _factory = new WebApplicationFactory<Program>();
        _client = _factory.CreateClient();
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private static object SmallOrder() => new
    {
        customerId = 1,
        customerName = "Ada Lovelace",
        customerEmail = "ada@example.com",
        lines = new[]
        {
            new { sku = "WIDGET-1", quantity = 2, unitPrice = 4.50m },
        },
    };

    [Test]
    public async Task Total_SumsLines()
    {
        var response = await _client.PostAsJsonAsync("/orders/total", SmallOrder());
        response.EnsureSuccessStatusCode();
        Assert.That(await response.Content.ReadFromJsonAsync<decimal>(), Is.EqualTo(9.00m));
    }

    [Test]
    public async Task Place_ReturnsReceiptWithSummary()
    {
        var response = await _client.PostAsJsonAsync("/orders", SmallOrder());
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        Assert.That(json, Does.Contain("Ada Lovelace"));
    }

    [Test]
    public async Task Estimate_SmallOrder_TwoDays()
    {
        var response = await _client.PostAsJsonAsync("/orders/estimate", SmallOrder());
        response.EnsureSuccessStatusCode();
        Assert.That(await response.Content.ReadFromJsonAsync<int>(), Is.EqualTo(2));
    }
}
