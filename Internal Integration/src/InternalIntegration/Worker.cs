using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InternalIntegration;

/// <summary>
/// Организует жизненный цикл службы: события папки ускоряют реакцию, а повторные
/// сканирования восстанавливают пропущенные события. Бизнес-логика находится в PairEngine.
/// </summary>
public sealed class Worker(ProcessingOptions options, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Движок удерживает блокировку входной папки до выхода из ExecuteAsync.
        using var engine = new PairEngine(options, logger);
        // Сохраняем только один сигнал: десять событий одной записи файла означают
        // необходимость одного сканирования, а не десяти параллельных обработок.
        var wakeups = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        using var watcher = new FileSystemWatcher(options.InputDirectory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            IncludeSubdirectories = false
        };
        // Обработчики событий ничего не читают и не переносят: только будят цикл.
        watcher.Created += (_, _) => wakeups.Writer.TryWrite(true);
        watcher.Changed += (_, _) => wakeups.Writer.TryWrite(true);
        watcher.Renamed += (_, _) => wakeups.Writer.TryWrite(true);
        watcher.Error += (_, _) => { logger.LogWarning("Ошибка FileSystemWatcher; состояние будет восстановлено сканированием"); wakeups.Writer.TryWrite(true); };
        // Подключаем наблюдение до начального сканирования, чтобы не пропустить
        // файл, который появится между запуском службы и завершением первого обхода.
        watcher.EnableRaisingEvents = true;
        logger.LogInformation("Обработчик запущен; вход {Input}, выход {Output}", options.InputDirectory, options.OutputDirectory);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Первый проход выполняется сразу; последующие - по событию либо таймеру.
                try { await engine.ScanAsync(DateTimeOffset.UtcNow, stoppingToken); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { logger.LogError("Ошибка доступа при сканировании/сохранении журнала: {Type}", e.GetType().Name); }
                // Отдельный токен позволяет различать обычный таймаут сканирования
                // и команду остановки всей службы от хоста.
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(TimeSpan.FromSeconds(options.ScanIntervalSeconds));
                try { await wakeups.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
            }
        }
        // Штатная остановка не является ошибкой и не должна порождать повторную попытку.
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        logger.LogInformation("Обработчик остановлен");
    }
}
