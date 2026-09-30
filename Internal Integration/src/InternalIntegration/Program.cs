using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using InternalIntegration;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args, ContentRootPath = AppContext.BaseDirectory
});
var options = builder.Configuration.GetSection("FileProcessing").Get<ProcessingOptions>() ?? new ProcessingOptions();
options.Validate();
builder.Services.AddSingleton(options);
builder.Services.AddWindowsService(o => o.ServiceName = "InternalIntegration");
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
