using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InternalIntegration;

public sealed class Worker(ProcessingOptions options, ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var engine = new PairEngine(options, logger);
        var wakeups = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        using var watcher = new FileSystemWatcher(options.InputDirectory)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
            IncludeSubdirectories = false
        };
        watcher.Created += (_, _) => wakeups.Writer.TryWrite(true);
        watcher.Changed += (_, _) => wakeups.Writer.TryWrite(true);
        watcher.Renamed += (_, _) => wakeups.Writer.TryWrite(true);
        watcher.Error += (_, _) => { logger.LogWarning("Ошибка FileSystemWatcher; состояние будет восстановлено сканированием"); wakeups.Writer.TryWrite(true); };
        watcher.EnableRaisingEvents = true;
        logger.LogInformation("Обработчик запущен; вход {Input}, выход {Output}", options.InputDirectory, options.OutputDirectory);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await engine.ScanAsync(DateTimeOffset.UtcNow, stoppingToken); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { logger.LogError("Ошибка доступа при сканировании/сохранении журнала: {Type}", e.GetType().Name); }
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                wait.CancelAfter(TimeSpan.FromSeconds(options.ScanIntervalSeconds));
                try { await wakeups.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested) { }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        logger.LogInformation("Обработчик остановлен");
    }
}
