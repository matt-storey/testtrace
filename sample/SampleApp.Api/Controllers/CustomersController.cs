using Microsoft.AspNetCore.Mvc;
using SampleApp.Domain;
using SampleApp.Services;

namespace SampleApp.Api.Controllers;

[ApiController]
[Route("customers")]
public class CustomersController : ControllerBase
{
    private readonly ICustomerService _customers;

    public CustomersController(ICustomerService customers) => _customers = customers;

    [HttpGet("display-name")]
    public ActionResult<string> DisplayName(int id, string name, string email)
    {
        var customer = new Customer(id, name, email);
        return Ok(_customers.GetDisplayName(customer));
    }
}
