using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;

namespace InternalIntegration;

/// <summary>Последовательная обработка: готовность → подготовка → публикация → удаление исходников.</summary>
public sealed class PairEngine : IDisposable
{
    private readonly ProcessingOptions options;
    private readonly FileLogger logger;
    private readonly StateStore store;
    private readonly FileStream instanceLock;
    private readonly Dictionary<string, (long Size, DateTime Write, DateTimeOffset Since)> observations = new(StringComparer.OrdinalIgnoreCase);
    private bool corruptStateReported;

    // Только для тестов восстановления: имитирует остановку между этапами записи на диск.
    public Action<string>? Checkpoint { get; set; }

    public PairEngine(ProcessingOptions options, FileLogger logger)
    {
        this.options = options;
        this.logger = logger;
        store = new StateStore(options.StateDirectory);
        // Файловая блокировка не допускает второй процесс с той же входной папкой.
        instanceLock = new FileStream(Path.Combine(options.InputDirectory, ".processor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>Один обход; вызывается только последовательно из Worker.</summary>
    public void Scan(DateTimeOffset now, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Dictionary<string, PairState> states;
        try { states = store.Load(); corruptStateReported = false; }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            // Без достоверного состояния нельзя отличить новую пару от прерванной публикации.
            if (!corruptStateReported) logger.Error("Поврежден журнал состояния; обработка приостановлена");
            corruptStateReported = true;
            return;
        }

        // Получаем список ОДИН раз за обход, вместо отдельного поиска PDF для каждого XML.
        var files = Directory.EnumerateFiles(options.InputDirectory)
            .ToDictionary(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase);
        foreach (var name in files.Keys.Where(n => Path.GetExtension(n).Equals(".xml", StringComparison.OrdinalIgnoreCase)))
        {
            token.ThrowIfCancellationRequested();
            if (states.ContainsKey(name) || !Ready(files[name], now)) continue;
            var state = new PairState { Name = name, FirstReady = now };
            store.Save(state); // С этого момента перезапуск не сбрасывает дедлайн PDF.
            states.Add(name, state);
        }

        foreach (var state in states.Values)
        {
            token.ThrowIfCancellationRequested();
            if (state.Blocked || state.RetryAfter > now) continue;
            try { Process(state, files, now, token); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException or ArgumentException or NotSupportedException)
            {
                state.Failures++;
                state.RetryAfter = now.AddSeconds(options.RetryDelaySeconds);
                state.Blocked = error is ConflictException or XmlException or ArgumentException or NotSupportedException ||
                    state.Failures >= options.MaxRetryAttempts;
                store.Save(state);
                // Логируем тип ошибки, но не Message парсера, содержащий части XML.
                Log(state, $"{(state.Blocked ? "Окончательная" : "Временная")} ошибка: {error.GetType().Name}; попытка={state.Failures}; исходники не удаляются", true);
            }
        }
        // Не накапливаем сведения о давно удаленных входных файлах.
        foreach (var path in observations.Keys.Where(p => !files.ContainsKey(Path.GetFileName(p))).ToArray())
            observations.Remove(path);
    }

    private void Process(PairState state, Dictionary<string, string> files, DateTimeOffset now, CancellationToken token)
    {
        var xml = Path.Combine(options.InputDirectory, state.Name);
        var expectedPdf = state.PdfName ?? Path.ChangeExtension(state.Name, ".pdf");
        var pdf = files.GetValueOrDefault(expectedPdf) ?? Path.Combine(options.InputDirectory, expectedPdf);
        if (state.Transaction == null)
        {
            if (!CheckFilesReady(state, xml, pdf, now)) return;
            token.ThrowIfCancellationRequested();
            PrepareFiles(state, xml, pdf);
        }
        token.ThrowIfCancellationRequested();
        PublishFiles(state);
        // Даже после Published повторяем проверку перед удалением любого исходника.
        Verify(Destination(state.Name), state.XmlHash!, state.XmlIdentity);
        if (state.PdfHash != null) Verify(Destination(state.PdfName ?? Path.ChangeExtension(state.Name, ".pdf")), state.PdfHash, state.PdfIdentity);
        LogResults(state);
        token.ThrowIfCancellationRequested();
        DeleteSources(state, xml, pdf);
        Log(state, "Обработка завершена; исходники удалены");
        // Журнал удаляется последним: сбой очистки всегда можно продолжить.
        if (Directory.Exists(Stage(state))) Directory.Delete(Stage(state), true);
        store.Delete(state);
    }

    private bool CheckFilesReady(PairState state, string xml, string pdf, DateTimeOffset now)
    {
        if (File.Exists(pdf) && !state.PdfSeen)
        {
            state.PdfSeen = true;
            state.PdfName = Path.GetFileName(pdf); // Сохраняем в том числе регистр расширения.
            store.Save(state);
        }
        if (!Ready(xml, now)) return false;
        // Уже замеченный PDF ожидаем даже после дедлайна. Отсутствующий PDF - до таймаута.
        return state.PdfSeen ? Ready(pdf, now) : now - state.FirstReady >= TimeSpan.FromSeconds(options.PdfWaitTimeoutSeconds);
    }

    private void PrepareFiles(PairState state, string xml, string pdf)
    {
        // Блокируем запись в исходники на все время чтения и подготовки результата.
        using var xmlStream = OpenSource(xml);
        using var pdfStream = state.PdfSeen ? OpenSource(pdf) : null;
        using var buffer = new MemoryStream();
        xmlStream.CopyTo(buffer);
        var source = buffer.ToArray();
        var result = new XmlTransformer().Transform(source);
        state.RequisitionIds = result.RequisitionIds;
        state.Results = result.Results;
        state.Replacements = result.Replacements;
        Log(state, "Начало обработки");
        if (result.AmbiguousTests > 0) Log(state, $"Неоднозначная принадлежность тестов: {result.AmbiguousTests}; для них замена не выполняется");
        if (File.Exists(Destination(state.Name)) || File.Exists(Destination(Path.GetFileName(pdf)))) throw new ConflictException();

        var transaction = Guid.NewGuid().ToString("N");
        var staging = Path.Combine(options.OutputDirectory, ".staging", transaction);
        Directory.CreateDirectory(staging);
        StateStore.WriteDurable(Path.Combine(staging, "xml"), result.Bytes);
        string? pdfHash = null;
        if (pdfStream != null)
        {
            // PDF копируем потоком. Ни разбора, ни генерации, ни модификации PDF здесь нет.
            using var output = new FileStream(Path.Combine(staging, "pdf"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            pdfStream.CopyTo(output);
            output.Flush(true);
            pdfStream.Position = 0;
            pdfHash = Convert.ToHexString(SHA256.HashData(pdfStream));
        }
        state.SourceXmlHash = Convert.ToHexString(SHA256.HashData(source));
        state.XmlHash = Convert.ToHexString(SHA256.HashData(result.Bytes));
        state.PdfHash = pdfHash;
        state.XmlIdentity = FileIdentity.Read(Path.Combine(staging, "xml"));
        state.PdfIdentity = pdfHash == null ? null : FileIdentity.Read(Path.Combine(staging, "pdf"));
        // Флаг готовности ставим последним, когда оба временных файла полностью записаны.
        state.Transaction = transaction;
        store.Save(state);
        Checkpoint?.Invoke("Prepared");
    }

    private void PublishFiles(PairState state)
    {
        if (state.Published) return;
        if (state.PdfHash != null)
            PublishOne(state, "pdf", state.PdfName ?? Path.ChangeExtension(state.Name, ".pdf"), state.PdfHash, state.PdfIdentity, state.PdfIntent);
        Checkpoint?.Invoke("AfterPdf");
        PublishOne(state, "xml", state.Name, state.XmlHash!, state.XmlIdentity, state.XmlIntent);
        state.Published = true;
        store.Save(state);
        Checkpoint?.Invoke("Published");
    }

    private void PublishOne(PairState state, string kind, string name, string hash, string? identity, bool intent)
    {
        var destination = Destination(name);
        if (File.Exists(destination))
        {
            // Для продолжения требуем свое намерение, совпадение байтов и объекта NTFS.
            // Чужой файл с теми же байтами все равно является конфликтом.
            if (!intent) throw new ConflictException();
            Verify(destination, hash, identity);
            return;
        }
        var stage = Path.Combine(Stage(state), kind);
        Verify(stage, hash, identity);
        if (kind == "pdf") state.PdfIntent = true; else state.XmlIntent = true;
        store.Save(state); // Сначала намерение на диск, только затем переименование.
        Checkpoint?.Invoke(kind + "Intent");
        File.Move(stage, destination, false);
    }

    private void LogResults(PairState state)
    {
        // Детали сохраняются в состоянии: после прерывания до лога они не теряются.
        // При сбое после записи лога возможен повтор записи результата при восстановлении.
        foreach (var result in state.Results)
        {
            if (result.Changes.Length == 0) logger.Info($"Без изменений: {result.Reason}", state.Name, result.RequisitionId);
            foreach (var change in result.Changes)
                logger.Info($"Изменение опубликовано: LaboratoryID={change.LaboratoryId}; {change.OldCode} -> {change.NewCode}; количество={change.Count}", state.Name, result.RequisitionId);
        }
        // Старые журналы не содержали деталей замен: не выдумываем их при миграции.
        if (state.Results.Length == 0)
            Log(state, state.Replacements == 0 ? "Без изменений" : $"Опубликован ранее подготовленный XML; замен={state.Replacements}; детали отсутствуют в старом состоянии");
        Log(state, state.PdfHash == null ? "XML опубликован без PDF: срок ожидания истек" : "Пара XML/PDF опубликована");
    }

    private void DeleteSources(PairState state, string xml, string pdf)
    {
        DeleteVerified(xml, state.SourceXmlHash!);
        Checkpoint?.Invoke("XmlDeleted");
        if (state.PdfHash != null) DeleteVerified(pdf, state.PdfHash);
    }

    private bool Ready(string path, DateTimeOffset now)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) { observations.Remove(path); return false; }
            if (!observations.TryGetValue(path, out var old) || old.Size != info.Length || old.Write != info.LastWriteTimeUtc)
            { observations[path] = (info.Length, info.LastWriteTimeUtc, now); return false; }
            if (now - old.Since < TimeSpan.FromSeconds(options.FileStabilitySeconds)) return false;
            using var stream = OpenSource(path);
            return stream.Length == old.Size && File.GetLastWriteTimeUtc(path) == old.Write;
        }
        // Еще поступающий файл пропускаем; следующий обход повторит проверку.
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private void Log(PairState state, string message, bool error = false)
    {
        foreach (var id in state.RequisitionIds.Length == 0 ? ["не определён"] : state.RequisitionIds)
            if (error) logger.Error(message, state.Name, id); else logger.Info(message, state.Name, id);
    }

    private static FileStream OpenSource(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    private string Destination(string name) => Path.Combine(options.OutputDirectory, name);
    private string Stage(PairState state) => Path.Combine(options.OutputDirectory, ".staging", state.Transaction!);

    private static void Verify(string path, string hash, string? identity)
    {
        using var stream = OpenSource(path);
        if (Convert.ToHexString(SHA256.HashData(stream)) != hash || FileIdentity.Read(path) != identity) throw new ConflictException();
    }

    private static void DeleteVerified(string path, string hash)
    {
        if (!File.Exists(path)) return; // Возможно, удаление уже произошло до перезапуска.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
        if (Convert.ToHexString(SHA256.HashData(stream)) != hash) throw new ConflictException();
        File.Delete(path);
    }

    public void Dispose() => instanceLock.Dispose();
}

public sealed class ConflictException : IOException;
