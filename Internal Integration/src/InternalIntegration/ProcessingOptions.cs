namespace InternalIntegration;

/// <summary>
/// Параметры раздела FileProcessing. Считываются один раз при старте.
/// Интервалы заданы в секундах, все рабочие пути после Validate становятся абсолютными.
/// </summary>
public sealed class ProcessingOptions
{
    // Три папки имеют разные роли: исходники, готовые результаты и журнал восстановления.
    public string InputDirectory { get; set; } = "Input";
    public string OutputDirectory { get; set; } = "Output";
    public string StateDirectory { get; set; } = "State";
    // Периодический обход нужен даже при исправном FileSystemWatcher: события могут теряться.
    public int ScanIntervalSeconds { get; set; } = 10;
    // Это минимальный интервал неизменности размера и LastWriteTime перед чтением.
    public int FileStabilitySeconds { get; set; } = 3;
    // Отсчитывается от сохраненного FirstReady, а не от каждого нового события папки.
    public int PdfWaitTimeoutSeconds { get; set; } = 300;
    public int RetryDelaySeconds { get; set; } = 5;
    public int MaxRetryAttempts { get; set; } = 5;

    /// <summary>Проверяет конфигурацию, создает папки и проверяет права записи/удаления.</summary>
    public void Validate()
    {
        InputDirectory = Resolve(InputDirectory);
        OutputDirectory = Resolve(OutputDirectory);
        StateDirectory = Resolve(StateDirectory);
        // Вложенные папки запрещены, чтобы собственные результаты/журналы не попадали во вход.
        var paths = new[] { InputDirectory, OutputDirectory, StateDirectory };
        for (var i = 0; i < paths.Length; i++)
        for (var j = i + 1; j < paths.Length; j++)
            if (Within(paths[i], paths[j]) || Within(paths[j], paths[i]))
                throw new ArgumentException("Папки Input, Output и State должны быть раздельными и не вложенными.");
        if (ScanIntervalSeconds <= 0 || FileStabilitySeconds <= 0 || PdfWaitTimeoutSeconds < 0 ||
            RetryDelaySeconds <= 0 || MaxRetryAttempts <= 0)
            throw new ArgumentException("Интервалы и число попыток должны быть положительными; таймаут PDF может быть нулевым.");
        foreach (var path in paths)
        {
            Directory.CreateDirectory(path);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Папки-ссылки не поддерживаются.");
            // Проверка только существования папки недостаточна: учетной записи службы
            // понадобятся права создавать и удалять файлы на всех этапах транзакции.
            var probe = Path.Combine(path, $".probe-{Guid.NewGuid():N}");
            using (File.Create(probe)) { }
            File.Delete(probe);
        }
    }
    // AppContext.BaseDirectory стабилен и при F5, и при запуске через Windows SCM.
    private static string Resolve(string path) => string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("Путь не может быть пустым.")
        : Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, AppContext.BaseDirectory));
    // Учитываем разделитель: C:\\Input2 не является дочерней папкой C:\\Input.
    private static bool Within(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
        a.StartsWith(Path.EndsInDirectorySeparator(b) ? b : b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
