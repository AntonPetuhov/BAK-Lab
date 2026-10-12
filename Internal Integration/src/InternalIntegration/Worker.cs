using Microsoft.Extensions.Hosting;

namespace InternalIntegration;

/// <summary>Один последовательный цикл: сканирование, обработка готовых пар, пауза.</summary>
public sealed class Worker(ProcessingOptions options, FileLogger logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var engine = new PairEngine(options, logger);
            logger.Info("Обработчик запущен");
            while (!stoppingToken.IsCancellationRequested)
            {
                // Первый обход сразу после запуска. Неготовая пара пропускается,
                // поэтому ожидание одного PDF не задерживает остальные заявки.
                try { engine.Scan(DateTimeOffset.UtcNow, stoppingToken); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { logger.Error($"Ошибка доступа при сканировании: {error.GetType().Name}"); }
                await Task.Delay(TimeSpan.FromSeconds(options.ScanIntervalSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            // Host остановит службу при выходе исключения из BackgroundService.
            Environment.ExitCode = 1;
            if (error is FileLogException) Console.Error.WriteLine("Недоступен файловый журнал. Обработка остановлена.");
            else logger.Error($"Обработчик остановлен из-за ошибки: {error.GetType().Name}");
            throw;
        }
        logger.Info("Обработчик остановлен");
    }
}
