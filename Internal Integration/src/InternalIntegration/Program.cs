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

    logger = new FileLogger(builder.Configuration["FileLogging:DirectoryPath"] ?? "Logs");

    var options = builder.Configuration.GetSection("FileProcessing")
        .Get<ProcessingOptions>() ?? new ProcessingOptions();
    options.Validate();

    builder.Services.AddSingleton(options);
    builder.Services.AddSingleton(logger);
    builder.Services.AddWindowsService(o => o.ServiceName = "InternalIntegration");
    // .NET Host использует logging внутри себя. Отключаем все выводы, включая EventLog,
    // добавляемый AddWindowsService. Прикладной код пишет только через FileLogger.
    builder.Logging.ClearProviders();
    builder.Services.AddHostedService<Worker>();
    using var host = builder.Build();
    logger.Info("Конфигурация проверена");
    await host.StartAsync();
    await host.WaitForShutdownAsync();
}
catch (Exception error)
{
    Environment.ExitCode = 1;
    // Не выводим текст исключения, который может содержать XML пациента.
    try { logger?.Error($"Приложение остановлено: {error.GetType().Name}"); }
    catch (FileLogException) { }
    Console.Error.WriteLine($"Internal Integration остановлен: {error.GetType().Name}. Проверьте настройки и права доступа.");
}
