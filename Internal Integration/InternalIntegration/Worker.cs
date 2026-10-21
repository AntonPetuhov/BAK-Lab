using Microsoft.Extensions.Hosting;

namespace InternalIntegration;

public class Worker(ProcessingOptions options, FileLogger logger) : BackgroundService
{
    private DateTime lastLogDeleting = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            //using var engine = new PairEngine(options, logger);
            var engine = new PairEngine(options, logger);

            logger.Service("Обработчик файлов запущен");

            while (!stoppingToken.IsCancellationRequested)
            {
                // Первый обход сразу после запуска. Неготовая пара пропускается,
                // поэтому ожидание одного PDF не задерживает остальные заявки.
                try 
                {
                    logger.Service("Мониторинг папки...");
                    engine.Scan(stoppingToken); 
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { 
                    logger.Error($"Ошибка доступа при сканировании: {error.GetType().Name}"); 
                }

                TryDeleteOldLogs();

                await Task.Delay(TimeSpan.FromSeconds(options.ScanIntervalSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception error)
        {
          logger.Error($"Обработчик остановлен из-за ошибки: {error.GetType().Name}");
        }

        logger.Service("Обработчик остановлен");
    }

    #region Очистка логов старше месяца
    private void TryDeleteOldLogs()
    {
        var now = DateTime.UtcNow;
        if (now - lastLogDeleting < TimeSpan.FromHours(24))
            return;

        lastLogDeleting = now;

        try
        {
            logger.DeleteOldLogs();
        }
        catch (Exception ex)
        {
            logger.Error($"Ошибка очистки логов: {ex.Message}");
        }
    }
    #endregion
}
