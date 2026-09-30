using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace InternalIntegration.Logging;

/// <summary>
/// Собственный провайдер ILogger: записывает текст UTF-8, создает новый файл по дате UTC
/// или при достижении заданного размера. Все категории используют одну очередь записи
/// через lock, поэтому строки от параллельных потоков не перемешиваются.
/// </summary>
[ProviderAlias("TextFile")]
public sealed class TextFileLoggerProvider : ILoggerProvider
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly TextFileLoggerOptions options;
    private readonly TimeProvider clock;
    private readonly object sync = new();
    private string day = "";
    private int part;
    private bool disposed;
    private long failedRecords;
    private DateTimeOffset? lastErrorReport;

    /// <summary>
    /// Проверяет доступ к журналу до начала обработки документов.
    /// TimeProvider позволяет тестам проверять смену суток без реального ожидания.
    /// </summary>
    public TextFileLoggerProvider(TextFileLoggerOptions options, TimeProvider? clock = null)
    {
        this.options = options.ValidateAndCopy();
        this.clock = clock ?? TimeProvider.System;
        if (!this.options.Enabled) return;

        Directory.CreateDirectory(this.options.DirectoryPath);
        // Ошибка первоначального открытия намеренно выходит наружу: запуск без
        // настроенного обязательного журнала должен быть заметен администратору.
        using var stream = OpenFile(this.clock.GetUtcNow(), 0);
    }

    public ILogger CreateLogger(string categoryName) => new TextFileLogger(this, categoryName);

    internal bool IsEnabled(LogLevel level) => options.Enabled && level != LogLevel.None;

    /// <summary>Формирует одну строку и синхронно дописывает ее на диск.</summary>
    internal void Write(string category, LogLevel level, EventId eventId, string message, Exception? exception)
    {
        lock (sync)
        {
            if (disposed || !IsEnabled(level)) return;
            var now = clock.GetUtcNow();
            // Тексты исключений могут содержать фрагменты XML с данными пациентов.
            // Записываем только имя типа; Message, StackTrace и Data не сериализуем.
            var exceptionType = exception == null ? "" : $" | ExceptionType={exception.GetType().FullName}";
            var line = $"{now:O} [{level}] {OneLine(category, 256)} [{eventId.Id}] " +
                OneLine(message, 8192) + exceptionType + Environment.NewLine;
            var bytes = Utf8.GetBytes(line);
            try
            {
                // После восстановления доступа честно сообщаем количество потерянных записей.
                // Самих сообщений в памяти не накапливаем, чтобы сбой диска не исчерпал память службы.
                if (failedRecords > 0)
                {
                    var notice = Utf8.GetBytes($"{now:O} [Warning] TextFileLogger [0] " +
                        $"Запись журнала восстановлена; пропущено сообщений: {failedRecords}.{Environment.NewLine}");
                    Append(now, notice);
                    failedRecords = 0;
                }
                Append(now, bytes);
                lastErrorReport = null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                failedRecords++;
                // Нельзя использовать ILogger внутри его собственного провайдера: это рекурсия.
                // Ошибка журнала после старта не должна прерывать публикацию XML/PDF.
                // В консоли оставляем короткое сообщение не чаще одного раза в минуту.
                if (lastErrorReport == null || now - lastErrorReport >= TimeSpan.FromMinutes(1))
                {
                    lastErrorReport = now;
                    try { Console.Error.WriteLine($"{now:O} TextFileLogger: ошибка записи ({error.GetType().Name}); сообщения могут быть потеряны."); }
                    catch (IOException) { /* stderr также может быть недоступен у службы. */ }
                }
            }
        }
    }

    private void Append(DateTimeOffset now, byte[] bytes)
    {
        using var stream = OpenFile(now, bytes.Length);
        stream.Write(bytes);
        // Буфер очищается после каждой строки: отдельного фонового потока и очереди нет.
        // Это упрощает остановку и не оставляет сообщения в памяти при штатном выходе.
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Подбирает часть дневного журнала и открывает ее для дописывания без перезаписи.</summary>
    private FileStream OpenFile(DateTimeOffset now, int incomingBytes)
    {
        var currentDay = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        if (day != currentDay) { day = currentDay; part = 0; }
        while (true)
        {
            var path = Path.Combine(options.DirectoryPath, $"{options.FileNamePrefix}-{day}-{part:D3}.log");
            // Разрешаем читать журнал Блокнотом, но не писать одновременно другому процессу.
            var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            if (stream.Length > 0 && (stream.Length >= options.MaxFileSizeBytes ||
                incomingBytes > options.MaxFileSizeBytes - stream.Length))
            {
                stream.Dispose();
                part++;
                continue;
            }
            // При перезапуске существующая часть дополняется, а не обнуляется.
            // Одна длинная запись не дробится и может превысить лимит пустой части.
            stream.Seek(0, SeekOrigin.End);
            return stream;
        }
    }

    /// <summary>Убирает управляющие символы, чтобы одно событие не подделывало несколько строк.</summary>
    private static string OneLine(string value, int maxLength)
    {
        var result = new StringBuilder();
        foreach (var character in value.Take(maxLength))
        {
            switch (character)
            {
                case '\r': result.Append("\\r"); break;
                case '\n': result.Append("\\n"); break;
                case '\t': result.Append("\\t"); break;
                default:
                    result.Append(char.IsControl(character) || character is '\u2028' or '\u2029' ? ' ' : character);
                    break;
            }
        }
        if (value.Length > maxLength) result.Append(" [обрезано]");
        return result.ToString();
    }

    /// <summary>Дожидается текущей записи; после освобождения провайдер больше не пишет.</summary>
    public void Dispose()
    {
        lock (sync) { disposed = true; }
    }

    /// <summary>Адаптер категории ILogger; файловыми ресурсами управляет общий провайдер.</summary>
    private sealed class TextFileLogger(TextFileLoggerProvider provider, string category) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => provider.IsEnabled(logLevel);

        // Произвольные scopes могут содержать данные пациентов: в текстовый файл их не выводим.
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            // Стандартный formatter подставляет значения {File}, {Count} и других
            // параметров шаблона. Фильтрация категорий выполняется LoggerFactory.
            provider.Write(category, logLevel, eventId, formatter(state, exception), exception);
        }
    }
}
