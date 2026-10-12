using System.Text;

namespace InternalIntegration;

/// <summary>Прямой текстовый журнал: один файл UTF-8 на сутки, без провайдеров и очередей.</summary>
public sealed class FileLogger
{
    private readonly string directory;
    private readonly TimeProvider clock;
    private readonly object sync = new();

    public FileLogger(string directory, TimeProvider? clock = null)
    {
        if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Не задан каталог журнала.");
        this.directory = Path.GetFullPath(directory, AppContext.BaseDirectory);
        this.clock = clock ?? TimeProvider.System;
        Directory.CreateDirectory(this.directory);
        Info("Журнал открыт");
    }

    public void Info(string message, string? file = null, string? requisitionId = null) => Write("INFO", message, file, requisitionId);
    public void Error(string message, string? file = null, string? requisitionId = null) => Write("ERROR", message, file, requisitionId);

    private void Write(string level, string message, string? file, string? requisitionId)
    {
        lock (sync)
        {
            // UTC указан явно: часовой пояс Windows не влияет на порядок записей.
            var now = clock.GetUtcNow();
            var line = $"{now:O} | {level} | file={Clean(file ?? "-")} | " +
                $"requisitionID={Clean(requisitionId ?? "не определён")} | {Clean(message)}{Environment.NewLine}";
            try
            {
                var path = Path.Combine(directory, $"InternalIntegration-{now:yyyyMMdd}.log");
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(Encoding.UTF8.GetBytes(line));
                stream.Flush(true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // При недоступности единственного журнала останавливаем обработку.
                // Исходники/состояние сохраняются для последующего восстановления.
                throw new FileLogException(error);
            }
        }
    }

    // Не даем значениям из XML/имен файлов вставить поддельную строку журнала.
    // Содержимое документов и Exception.Message вызывающий код сюда не передает.
    private static string Clean(string value) => value.Replace("\r", "\\r").Replace("\n", "\\n")
        .Replace("\t", "\\t").Replace("|", "\\u007C").Replace("\u2028", " ").Replace("\u2029", " ");
}

public sealed class FileLogException(Exception inner) : Exception("Не удалось записать текстовый журнал.", inner);
