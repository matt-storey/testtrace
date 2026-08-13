using SampleApp.Common;
using SampleApp.Domain;

namespace SampleApp.Services;

public class CustomerService : ICustomerService
{
    public string GetDisplayName(Customer customer)
    {
        var name = StringUtils.Normalise(customer.Name);
        return name.Length == 0 ? $"Customer #{customer.Id}" : name;
    }

    public bool IsContactable(Customer customer) =>
        customer.Email.Contains('@');
}
