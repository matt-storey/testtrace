using SampleApp.Domain;

namespace SampleApp.Services;

public interface ICustomerService
{
    string GetDisplayName(Customer customer);
    bool IsContactable(Customer customer);
}
