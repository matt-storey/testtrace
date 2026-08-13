using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;

namespace SampleApp.Api.Tests;

[TestFixture]
public class CustomersControllerTests
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

    [Test]
    public async Task DisplayName_NormalisesName()
    {
        var response = await _client.GetAsync("/customers/display-name?id=1&name=%20Grace%20%20Hopper%20&email=g%40example.com");
        response.EnsureSuccessStatusCode();
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo("Grace Hopper"));
    }
}
