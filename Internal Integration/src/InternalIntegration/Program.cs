using InternalIntegration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

FileLogger? logger = null;
try
{

    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args, ContentRootPath = AppContext.BaseDirectory // Конфигурация читается рядом с exe, независимо от рабочей папки службы Windows.
    });

    // логгер
    logger = new FileLogger(builder.Configuration["FileLogging:DirectoryPath"] ?? "Logs");

    // Получаем секцию конфигурации с именем FileProcessing.
    var fileProcessingSection = builder.Configuration.GetSection("FileProcessing");
    // Привязываем секцию к объекту ProcessingOptions.
    var options = fileProcessingSection.Get<ProcessingOptions>();
    // Если секции нет или привязка не удалась, то создаём объект со значениями настроек по умолчанию.
    if (options == null)
    {
        options = new ProcessingOptions();
    }
    // Проверяем и нормализуем настройки.
    options.Validate(); // Если настройки неверные, служба не запуститься, исключение в event log

    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(logger);
    builder.Services.AddWindowsService(o => o.ServiceName = "InternalIntegration");
    builder.Services.AddHostedService<Worker>();

    using var host = builder.Build();
    logger.Service("Конфигурация проверена");
    logger.Service("Запуск службы");

    host.Run();
}
catch (Exception error)
{

    logger?.Error($"Приложение остановлено: {error.Message}"); 

}
