using System.Security.Cryptography;
using System.Xml;

namespace InternalIntegration;

// Один последовательный обход папки
public sealed class PairEngine : IDisposable
{
    private readonly ProcessingOptions options;
    private readonly FileLogger logger;
    private readonly FileStream instanceLock;
    private readonly Dictionary<string, PendingXml> pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Size, DateTime Write, DateTimeOffset Since)> observations = new(StringComparer.OrdinalIgnoreCase);

    // Позволяет тесту прервать публикацию в нужном месте без остановки настоящей службы.
    public Action<string>? Checkpoint { get; set; }

    public PairEngine(ProcessingOptions options, FileLogger logger)
    {
        this.options = options;
        this.logger = logger;
        // Второй процесс не должен одновременно обрабатывать ту же входную папку.
        instanceLock = new FileStream(Path.Combine(options.InputDirectory, ".processor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public void Scan(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        var allFiles = Directory.GetFiles(options.InputDirectory);
        // ключ - имя файла, значение - полный путь к файлу
        var files = allFiles.ToDictionary(filePath => Path.GetFileName(filePath), filePath => filePath, StringComparer.OrdinalIgnoreCase);

        // отберем только xml файлы
        var xmlFiles = allFiles.Where(path => string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase));
        // проходимся по каждому
        foreach (var xml in xmlFiles)
        {
            token.ThrowIfCancellationRequested();

            
        }

        foreach (var xml in files.Values.Where(p => Path.GetExtension(p).Equals(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(xml);
            if (!pending.TryGetValue(name, out var item)) pending[name] = item = new PendingXml();
            if (item.Blocked || item.RetryAfter > now) continue;
            try 
            { 
                Process(xml, item, files, now, token); 
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException or ArgumentException or NotSupportedException)
            {
                item.Failures++;
                item.RetryAfter = now.AddSeconds(options.RetryDelaySeconds);
                item.Blocked = item.PublicationStarted || error is ConflictException or XmlException or ArgumentException or NotSupportedException ||
                    item.Failures >= options.MaxRetryAttempts;
                // Не пишем Message исключения: сообщение XML-парсера может содержать данные пациента.
                Log(name, item, $"{(item.Blocked ? "Окончательная" : "Временная")} ошибка: {error.GetType().Name}; попытка={item.Failures}; " +
                    (item.PublicationStarted ? "публикация прервана, требуется ручной разбор" : "исходники не удаляются"), true);
            }
        }
        // Удаленные входные файлы не должны оставлять растущий список ожиданий в памяти.
        foreach (var name in pending.Keys.Where(n => !files.ContainsKey(n)).ToArray()) pending.Remove(name);
        foreach (var path in observations.Keys.Where(p => !files.ContainsKey(Path.GetFileName(p))).ToArray()) observations.Remove(path);
    }

    private void Process(string xml, PendingXml item, Dictionary<string, string> files, DateTimeOffset now, CancellationToken token)
    {
        var name = Path.GetFileName(xml);
        if (item.Result == null)
        {
            if (!Ready(xml, now)) return;
            // Разбор выполняется ДО ожидания PDF. Исходный XML здесь не изменяется.
            using var source = OpenSource(xml);
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var bytes = buffer.ToArray();
            item.Result = new XmlTransformer().Transform(bytes);
            item.SourceHash = SHA256.HashData(bytes);
            item.PreparedAt = now;
            Log(name, item, "Начало обработки; XML подготовлен в памяти");
            foreach (var result in item.Result.Results)
            {
                if (result.Changes.Length == 0) logger.Info($"Без изменений: {result.Reason}", name, result.RequisitionId);
                foreach (var change in result.Changes)
                    logger.Info($"Подготовлена замена: LaboratoryID={change.LaboratoryId}; {change.OldCode} -> {change.NewCode}; количество={change.Count}", name, result.RequisitionId);
            }
            if (item.Result.Results.Length == 0) Log(name, item, "Без изменений: заявки не найдены");
            if (item.Result.AmbiguousTests > 0) Log(name, item, $"Неоднозначная принадлежность тестов: {item.Result.AmbiguousTests}; замены для них не выполняются");
        }

        var pdf = files.GetValueOrDefault(Path.ChangeExtension(name, ".pdf"));
        // PDF мог появиться уже после получения списка файлов в начале обхода.
        var expectedPdf = Path.ChangeExtension(xml, ".pdf");
        if (pdf == null && File.Exists(expectedPdf)) pdf = expectedPdf;
        // Запоминаем появление PDF, чтобы не отправить XML отдельно, пока PDF дописывается.
        item.PdfSeen |= pdf != null;
        if (item.PdfSeen)
        {
            if (pdf == null || !Ready(pdf, now)) return;
        }
        else if (now - item.PreparedAt < TimeSpan.FromSeconds(options.PdfWaitTimeoutSeconds)) return;

        token.ThrowIfCancellationRequested();
        Publish(xml, pdf, item, token);
    }

    private void Publish(string xml, string? pdf, PendingXml item, CancellationToken token)
    {
        var name = Path.GetFileName(xml);
        // На время публикации запрещаем запись в источники; разрешаем их итоговое удаление.
        using var xmlSource = OpenSource(xml, allowDelete: true);
        if (!SHA256.HashData(xmlSource).AsSpan().SequenceEqual(item.SourceHash))
        {
            // Если отправитель изменил XML во время ожидания, старый результат нельзя публиковать.
            Log(name, item, "XML изменился во время ожидания; подготовка и ожидание будут повторены");
            pending.Remove(name);
            observations.Remove(xml);
            return;
        }
        using var pdfSource = pdf == null ? null : OpenSource(pdf, allowDelete: true);
        var outputXml = Path.Combine(options.OutputDirectory, name);
        var outputPdf = Path.Combine(options.OutputDirectory, Path.GetFileName(pdf ?? Path.ChangeExtension(xml, ".pdf")));
        // Даже совпадающие байты не делают существующий файл нашей незавершенной публикацией.
        if (File.Exists(outputXml) || File.Exists(outputPdf) || Directory.Exists(outputXml) || Directory.Exists(outputPdf)) throw new ConflictException();
        var tempXml = Path.Combine(options.OutputDirectory, $".{Guid.NewGuid():N}.xml.tmp");
        var tempPdf = Path.Combine(options.OutputDirectory, $".{Guid.NewGuid():N}.pdf.tmp");
        try
        {
            using (var output = new FileStream(tempXml, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(item.Result!.Bytes); output.Flush(true); }
            if (pdfSource != null)
            {
                using var output = new FileStream(tempPdf, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                // PDF копируется как поток байтов, его содержимое не разбирается.
                pdfSource.CopyTo(output);
                output.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            Checkpoint?.Invoke("Prepared");
            if (pdf != null)
            {
                File.Move(tempPdf, outputPdf, false);
                item.PublicationStarted = true;
                Checkpoint?.Invoke("AfterPdf");
            }
            // Появление итогового XML означает, что PDF (если он есть) уже опубликован.
            File.Move(tempXml, outputXml, false);
            item.PublicationStarted = true;
            Checkpoint?.Invoke("Published");
            Log(name, item, (pdf == null ? "XML опубликован без PDF: срок ожидания истек" : "Пара XML/PDF опубликована") + $"; замен={item.Result!.Replacements}");
            // Только после успешной публикации всех результатов разрешено удалять исходники.
            File.Delete(xml);
            if (pdf != null) File.Delete(pdf);
            Log(name, item, "Обработка завершена; исходники удалены");
            pending.Remove(name);
        }
        finally
        {
            // При обычной ошибке убираем только свои временные файлы, итоговые не трогаем.
            // После аварийного завершения процесса временные файлы могут остаться на диске.
            File.Delete(tempXml);
            File.Delete(tempPdf);
        }
    }

    private bool Ready(string path, DateTimeOffset now)
    {
        var info = new FileInfo(path);
        if (!info.Exists) { observations.Remove(path); return false; }
        if (!observations.TryGetValue(path, out var old) || old.Size != info.Length || old.Write != info.LastWriteTimeUtc)
        { observations[path] = (info.Length, info.LastWriteTimeUtc, now); return false; }
        if (now - old.Since < TimeSpan.FromSeconds(options.FileStabilitySeconds)) return false;
        try
        {
            using var stream = OpenSource(path);
            return stream.Length == old.Size && File.GetLastWriteTimeUtc(path) == old.Write;
        }
        // Занятый файл еще не готов. Это ожидание, а не неудачная попытка публикации.
        catch (IOException) { return false; }
    }

    private void Log(string name, PendingXml item, string message, bool error = false)
    {
        var ids = item.Result?.RequisitionIds ?? [];
        foreach (var id in ids.Length == 0 ? ["не определён"] : ids)
            if (error) logger.Error(message, name, id); else logger.Info(message, name, id);
    }

    private static FileStream OpenSource(string path, bool allowDelete = false) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.Read | (allowDelete ? FileShare.Delete : FileShare.None));

    public void Dispose() => instanceLock.Dispose();

    // Это только память текущего запуска. JSON, этапов восстановления и идентификаторов NTFS нет.
    private sealed class PendingXml
    {
        public TransformResult? Result;
        public byte[]? SourceHash;
        public DateTimeOffset PreparedAt;
        public bool PdfSeen;
        public int Failures;
        public DateTimeOffset RetryAfter;
        public bool Blocked;
        public bool PublicationStarted;
    }
}

public sealed class ConflictException : IOException;
