using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using InternalIntegration;
using InternalIntegration.Logging;

// Точка входа одинакова для консоли и службы Windows. Явный ContentRootPath
// позволяет читать appsettings.json рядом с exe, а не из системной папки SCM.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args, ContentRootPath = AppContext.BaseDirectory
});

// Подключаем собственный провайдер дополнительно к Console/Debug/EventLog.
// using гарантирует освобождение провайдера и при ошибке запуска хоста.
var fileLogging = builder.Configuration.GetSection("FileLogging").Get<TextFileLoggerOptions>() ?? new TextFileLoggerOptions();
using var fileLogger = new TextFileLoggerProvider(fileLogging);
builder.Logging.AddProvider(fileLogger);

// Настройки обработки регистрируются одним экземпляром и не меняются на лету.
// После редактирования путей/интервалов приложение необходимо перезапустить.
var options = builder.Configuration.GetSection("FileProcessing").Get<ProcessingOptions>() ?? new ProcessingOptions();
builder.Services.AddSingleton(options);
// AddWindowsService определяет запуск через SCM; при F5 остается консольный режим.
builder.Services.AddWindowsService(o => o.ServiceName = "InternalIntegration");
builder.Services.AddHostedService<Worker>();

using var host = builder.Build();
var startupLogger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("InternalIntegration.Startup");
try
{
    // Проверяем папки до запуска Worker. На этом этапе файловый журнал уже доступен,
    // поэтому неправильные пути/права также фиксируются в нем без текста исключения.
    options.Validate();
    startupLogger.LogInformation("Конфигурация проверена; запуск Internal Integration");
    // Хост управляет BackgroundService и передает отмену при Ctrl+C или остановке SCM.
    await host.StartAsync();
    await host.WaitForShutdownAsync();
}
catch (Exception exception)
{
    startupLogger.LogCritical("Запуск или работа приложения завершились ошибкой {Type}", exception.GetType().Name);
    // Не пробрасываем исключение в необработанный вывод: его текст может содержать
    // данные входного документа. Ненулевой код сообщает SCM/оператору об ошибке.
    Environment.ExitCode = 1;
}
