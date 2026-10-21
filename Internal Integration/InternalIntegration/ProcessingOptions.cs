namespace InternalIntegration;

// Класс с настройками
public class ProcessingOptions
{
    // Папка с исходными файлами, которые формирует CGM
    public string InputDirectory { get; set; } = "Input";
    // Папка с преобразованными файлами (если требуется), для последующей отправки в МИС
    public string OutputDirectory { get; set; } = "Output";
    // Папка для копирования
    public string CopyToDirectory { get; set; } = "Copy";
    // Пауза между последовательными обходами
    public int ScanIntervalSeconds { get; set; } = 1;
    // Это минимальный интервал неизменности размера и LastWriteTime перед чтением.
    public int FileStabilitySeconds { get; set; } = 3;
    // Отсчет после подготовки XML; после перезапуска начинается заново.
    public int PdfWaitTimeoutSeconds { get; set; } = 10;
    public int RetryDelaySeconds { get; set; } = 5;
    public int MaxRetryAttempts { get; set; } = 5;

    // Проверяем конфигурацию
    public void Validate()
    {
        // проверяем папки
        InputDirectory = Resolve(InputDirectory);
        OutputDirectory = Resolve(OutputDirectory);

        // Вложенные папки запрещены, чтобы собственные результаты/журналы не попадали во вход.
        var paths = new[] { InputDirectory, OutputDirectory };

        if (ScanIntervalSeconds <= 0 || FileStabilitySeconds <= 0 || PdfWaitTimeoutSeconds < 0 || RetryDelaySeconds <= 0 || MaxRetryAttempts <= 0)
            throw new ArgumentException("Временные интервалы и число попыток должны быть положительными");

        foreach (var path in paths)
        {
            Directory.CreateDirectory(path);
            // Проверка только существования папки недостаточна
            // УЗ службы понадобятся права создавать и удалять файлы
            var probe = Path.Combine(path, $".probe");
            using (File.Create(probe)) { }
            File.Delete(probe);
        }
    }

    // Проверка пути к папке
    private static string Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Путь не может быть пустым. Требуется указать путь к папке.");
        }

        // абсолютный путь
        string fullPath = Path.GetFullPath(path, AppContext.BaseDirectory);
        // без завершающего разделителя
        string trimmedPath = Path.TrimEndingDirectorySeparator(fullPath);

        return trimmedPath;
    }

}
