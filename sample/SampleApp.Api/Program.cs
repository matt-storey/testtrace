using SampleApp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddSingleton<IOrderService, OrderService>();
builder.Services.AddSingleton<ICustomerService, CustomerService>();

var app = builder.Build();

app.MapControllers();

app.Run();

public partial class Program;
