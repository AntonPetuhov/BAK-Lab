using Xunit;

namespace InternalIntegration.Tests;

/// <summary>Проверяем настоящий текстовый файл, а не имитацию стороннего ILogger.</summary>
public sealed class FileLoggerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "InternalIntegration-logs-" + Guid.NewGuid().ToString("N"));
    private readonly FixedClock clock = new();

    [Fact] public void AppendsUtf8AndKeepsOneLinePerEvent()
    {
        var logger = new FileLogger(root, clock);
        logger.Info("Без изменений\r\nподделка", "a.xml", "test-1");
        new FileLogger(root, clock).Error("Ошибка", "b.xml", "test-2");
        var lines = File.ReadAllLines(Directory.GetFiles(root).Single());
        Assert.Equal(4, lines.Length);
        Assert.Contains("requisitionID=test-1 | Без изменений\\r\\nподделка", lines[1]);
        Assert.Contains("ERROR | file=b.xml | requisitionID=test-2", lines[3]);
    }

    [Fact] public void RollsOnUtcDate()
    {
        var logger = new FileLogger(root, clock);
        clock.Now = clock.Now.AddDays(1); logger.Info("Новый день");
        Assert.True(File.Exists(Path.Combine(root, "InternalIntegration-20261012.log")));
        Assert.True(File.Exists(Path.Combine(root, "InternalIntegration-20261013.log")));
    }

    [Fact] public void WriteFailureIsExplicitAndNextWriteCanRecover()
    {
        var logger = new FileLogger(root, clock);
        using (var locked = new FileStream(Directory.GetFiles(root).Single(), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Throws<FileLogException>(() => logger.Info("Недоступно"));
        logger.Info("Доступ восстановлен");
        Assert.Contains("Доступ восстановлен", File.ReadAllText(Directory.GetFiles(root).Single()));
    }

    [Fact] public void ParallelCallersDoNotMixLines()
    {
        var logger = new FileLogger(root, clock);
        Parallel.For(0, 30, i => logger.Info($"Запись {i}", $"{i}.xml", $"req-{i}"));
        Assert.Equal(31, File.ReadAllLines(Directory.GetFiles(root).Single()).Length);
    }

    private sealed class FixedClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
