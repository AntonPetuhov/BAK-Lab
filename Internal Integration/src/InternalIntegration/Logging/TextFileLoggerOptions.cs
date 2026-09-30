namespace InternalIntegration.Logging;

/// <summary>
/// Настройки собственного файлового журнала из раздела FileLogging.
/// Уровни сообщений задаются стандартным разделом Logging:TextFile:LogLevel.
/// </summary>
public sealed class TextFileLoggerOptions
{
    public bool Enabled { get; set; } = true;
    public string DirectoryPath { get; set; } = "Logs";
    public string FileNamePrefix { get; set; } = "InternalIntegration";
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>Проверяет значения и создает независимую копию настроек на время работы процесса.</summary>
    public TextFileLoggerOptions ValidateAndCopy()
    {
        if (Enabled)
        {
            if (string.IsNullOrWhiteSpace(DirectoryPath))
                throw new ArgumentException("FileLogging:DirectoryPath не может быть пустым.");
            // Префикс используется только как имя, а не путь: запрещаем выход из папки журнала.
            if (string.IsNullOrWhiteSpace(FileNamePrefix) || FileNamePrefix.Length > 80 ||
                FileNamePrefix.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
                throw new ArgumentException("FileLogging:FileNamePrefix: разрешены латинские буквы, цифры, '-' и '_', до 80 символов.");
            if (MaxFileSizeBytes < 1024)
                throw new ArgumentException("FileLogging:MaxFileSizeBytes должен быть не меньше 1024.");
        }

        return new TextFileLoggerOptions
        {
            Enabled = Enabled,
            // Рабочий каталог Windows Service обычно отличается от каталога exe.
            // Относительный путь всегда привязываем именно к расположению приложения.
            DirectoryPath = Enabled ? Path.GetFullPath(DirectoryPath, AppContext.BaseDirectory) : DirectoryPath,
            FileNamePrefix = FileNamePrefix,
            MaxFileSizeBytes = MaxFileSizeBytes
        };
    }
}
