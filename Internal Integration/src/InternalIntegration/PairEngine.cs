using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace InternalIntegration;

/// <summary>
/// Постоянное состояние одной пары. Сериализуется в JSON после каждого важного этапа.
/// Это журнал восстановления, а не текстовый лог: удалять его до окончания задания нельзя.
/// </summary>
public sealed class PairState
{
    // Исходное имя XML и время, когда его впервые признали завершенным.
    public string Name { get; set; } = "";
    public DateTimeOffset FirstReady { get; set; }
    // Факт появления PDF сохраняется навсегда для задания: таймаут не должен
    // разрешать публикацию XML без уже обнаруженного, но еще записываемого PDF.
    public bool PdfSeen { get; set; }
    public string? PdfName { get; set; }
    // null означает ожидание файлов. GUID означает, что подготовлен каталог .staging.
    public string? Transaction { get; set; }
    // Хеш исходного XML нужен перед удалением, хеш результата - при восстановлении публикации.
    public string? SourceXmlHash { get; set; }
    public string? XmlHash { get; set; }
    public string? PdfHash { get; set; }
    // Идентификаторы NTFS отличают наши переименованные файлы от чужих с теми же байтами.
    public string? XmlIdentity { get; set; }
    public string? PdfIdentity { get; set; }
    public string[] RequisitionIds { get; set; } = [];
    public int Replacements { get; set; }
    // Intent записывается ДО переноса. После сбоя он разрешает проверить уже видимый
    // результат по хешу и FileIdentity вместо признания его обычным конфликтом имен.
    public bool PdfIntent { get; set; }
    public bool XmlIntent { get; set; }
    // После Published остается только проверить результаты и удалить исходники/журнал.
    public bool Published { get; set; }
    // Блокировка постоянна между перезапусками; оператор снимает ее после устранения причины.
    public int Failures { get; set; }
    public DateTimeOffset RetryAfter { get; set; }
    public bool Blocked { get; set; }
}

/// <summary>
/// Последовательно проводит пары через ожидание, подготовку, публикацию и очистку.
/// Каждое сканирование сначала восстанавливает журнал, затем обнаруживает новые XML.
/// </summary>
public sealed class PairEngine : IDisposable
{
    private readonly ProcessingOptions options;
    private readonly ILogger logger;
    private readonly FileStream instanceLock;
    // Защита внутри процесса дополняет файловую блокировку от второго процесса.
    private readonly SemaphoreSlim gate = new(1, 1);
    // Наблюдения стабильности живут в памяти; после перезапуска они собираются заново.
    // Сохраненный дедлайн PDF при этом не меняется.
    private readonly Dictionary<string, (long Size, DateTime Write, DateTimeOffset Since)> observations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> corruptStates = new(StringComparer.OrdinalIgnoreCase);
    // Тестовая точка: позволяет имитировать падение строго между устойчивыми этапами.
    public Action<string>? Checkpoint { get; set; }

    public PairEngine(ProcessingOptions options, ILogger logger)
    {
        this.options = options;
        this.logger = logger;
        // Только один процесс на входную папку, даже если ему задали другой StateDirectory.
        instanceLock = new FileStream(Path.Combine(options.InputDirectory, ".processor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    /// <summary>
    /// Выполняет один проход без ожидания поступления файлов. now передается извне,
    /// чтобы тесты могли перемещать время без реальных пятиминутных задержек.
    /// </summary>
    public async Task ScanAsync(DateTimeOffset now, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            // Этап 1: сначала загружаем незавершенные задания, включая те, у которых
            // XML уже удален, а очистка после публикации была прервана.
            var states = new Dictionary<string, PairState>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(options.StateDirectory, "*.json"))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var state = JsonSerializer.Deserialize<PairState>(File.ReadAllBytes(path)) ?? throw new JsonException();
                    // Не доверяем произвольным путям из JSON: допустимы только имена
                    // файлов и GUID транзакции. Иначе можно обратиться вне рабочих папок.
                    if (state.Name != Path.GetFileName(state.Name) || string.IsNullOrWhiteSpace(state.Name) ||
                        Path.GetFullPath(path) != Path.GetFullPath(StatePath(state.Name)) ||
                        (state.PdfName != null && state.PdfName != Path.GetFileName(state.PdfName)) ||
                        (state.Transaction != null && !Guid.TryParseExact(state.Transaction, "N", out _))) throw new JsonException();
                    states.Add(state.Name, state);
                }
                catch (Exception e) when (e is JsonException or ArgumentException)
                {
                    // При повреждении журнала нельзя безопасно отличить новую пару от
                    // уже опубликованной, поэтому приостанавливаем весь проход.
                    if (corruptStates.Add(path)) logger.LogError("Поврежден журнал состояния {File}; обработка приостановлена", Path.GetFileName(path));
                    return;
                }
            }
            // Этап 2: новые задания создаем только после подтвержденной готовности XML.
            foreach (var xml in Directory.EnumerateFiles(options.InputDirectory).Where(p => Path.GetExtension(p).Equals(".xml", StringComparison.OrdinalIgnoreCase)))
            {
                // Сохраняем исходный регистр имени; поиск одноименного PDF нечувствителен к регистру.
                var name = Path.GetFileName(xml);
                if (!states.ContainsKey(name))
                {
                    try
                    {
                        if (!Ready(xml, now)) continue;
                        // FirstReady сохраняется только при создании; повторные события
                        // больше не могут начать срок ожидания PDF с нуля.
                        var state = new PairState { Name = name, FirstReady = now };
                        Save(state);
                        states.Add(name, state);
                    }
                    catch (IOException) { logger.LogWarning("XML пока недоступен: {File}", name); }
                    catch (UnauthorizedAccessException) { logger.LogWarning("Нет доступа к XML: {File}", name); }
                }
            }
            // Этап 3: независимая ошибка одного задания не мешает остальным парам.
            foreach (var state in states.Values)
            {
                token.ThrowIfCancellationRequested();
                if (state.Blocked || state.RetryAfter > now) continue;
                try { Process(state, now, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException or NotSupportedException)
                {
                    state.Failures++;
                    state.RetryAfter = now.AddSeconds(options.RetryDelaySeconds);
                    state.Blocked = e is ConflictException or System.Xml.XmlException or ArgumentException or NotSupportedException || state.Failures >= options.MaxRetryAttempts;
                    Save(state);
                    // Тексты исключений парсера могут включать XML пациента: логируем только тип.
                    if (state.Blocked) logger.LogError("Окончательная ошибка {Type}, файл {File}; исходники сохранены, попыток {Attempts}", e.GetType().Name, state.Name, state.Failures);
                    else logger.LogWarning("Временная ошибка {Type}, файл {File}, попытка {Attempt}", e.GetType().Name, state.Name, state.Failures);
                }
            }
        }
        finally { gate.Release(); }
    }

    /// <summary>Продвигает одно задание настолько далеко, насколько позволяют готовность файлов и журнал.</summary>
    private void Process(PairState s, DateTimeOffset now, CancellationToken token)
    {
        var xml = Path.Combine(options.InputDirectory, s.Name);
        var pdfName = s.PdfName ?? Path.ChangeExtension(s.Name, ".pdf");
        // Берем реальное имя PDF, чтобы не потерять исходный регистр расширения.
        var pdf = Directory.EnumerateFiles(options.InputDirectory).FirstOrDefault(p => Path.GetFileName(p).Equals(pdfName, StringComparison.OrdinalIgnoreCase))
            ?? Path.Combine(options.InputDirectory, pdfName);
        // Подготовка выполняется только один раз. После появления Transaction
        // восстановление использует уже подготовленные данные и контрольные суммы.
        if (s.Transaction == null)
        {
            if (File.Exists(pdf) && !s.PdfSeen) { s.PdfSeen = true; s.PdfName = Path.GetFileName(pdf); pdfName = s.PdfName; Save(s); }
            if (!Ready(xml, now)) return;
            if (s.PdfSeen)
            {
                if (!Ready(pdf, now)) return; // Обнаруженный PDF ожидаем и после дедлайна.
            }
            else if (now - s.FirstReady < TimeSpan.FromSeconds(options.PdfWaitTimeoutSeconds)) return;
            token.ThrowIfCancellationRequested();
            // Удерживаем запрет записи на оба исходника до окончания подготовки.
            using var xmlStream = OpenSource(xml);
            using var pdfStream = s.PdfSeen ? OpenSource(pdf) : null;
            var bytes = ReadAll(xmlStream);
            var result = new XmlTransformer().Transform(bytes);
            logger.LogInformation("Начало обработки {File}; замен {Count}", s.Name, result.Replacements);
            if (result.AmbiguousTests > 0) logger.LogWarning("Неоднозначная принадлежность к отделению: {File}, тестов {Count}; эти тесты не изменены", s.Name, result.AmbiguousTests);
            // Проверяем оба имени заранее; чужие результаты никогда не перезаписываем.
            if (File.Exists(Destination(s.Name)) || File.Exists(Destination(pdfName))) throw new ConflictException();
            // Временные результаты размещаем на том же томе, что и выход: последующее
            // переименование отдельного файла не требует копирования между дисками.
            var id = Guid.NewGuid().ToString("N");
            var staging = Path.Combine(options.OutputDirectory, ".staging", id);
            Directory.CreateDirectory(staging);
            WriteDurable(Path.Combine(staging, "xml"), result.Bytes);
            string? pdfHash = null;
            if (pdfStream != null)
            {
                using var output = new FileStream(Path.Combine(staging, "pdf"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                // PDF копируется потоком без разбора формата и без изменения байтов.
                pdfStream.CopyTo(output);
                output.Flush(true);
                pdfStream.Position = 0;
                pdfHash = Convert.ToHexString(SHA256.HashData(pdfStream));
            }
            // Только когда оба временных результата сброшены на диск, фиксируем
            // их хеши и идентификаторы в журнале восстановления.
            s.Transaction = id;
            s.SourceXmlHash = Hash(bytes);
            s.XmlHash = Hash(result.Bytes);
            s.PdfHash = pdfHash;
            s.XmlIdentity = FileIdentity.Read(Path.Combine(staging, "xml"));
            s.PdfIdentity = pdfHash == null ? null : FileIdentity.Read(Path.Combine(staging, "pdf"));
            s.Replacements = result.Replacements;
            s.RequisitionIds = result.RequisitionIds;
            Save(s);
            Checkpoint?.Invoke("Prepared");
        }
        token.ThrowIfCancellationRequested();
        // Порядок публикации является контрактом: появление XML служит сигналом готовности.
        if (!s.Published)
        {
            if (s.PdfHash != null) Publish(s, "pdf", pdfName, s.PdfHash, s.PdfIntent, () => s.PdfIntent = true);
            Checkpoint?.Invoke("AfterPdf");
            Publish(s, "xml", s.Name, s.XmlHash!, s.XmlIntent, () => s.XmlIntent = true);
            s.Published = true;
            Save(s);
            Checkpoint?.Invoke("Published");
        }
        // Повторно проверяем оба результата перед удалением любого исходника,
        // в том числе при восстановлении после перезапуска службы.
        Verify(Destination(s.Name), s.XmlHash!);
        VerifyIdentity(Destination(s.Name), s.XmlIdentity!);
        if (s.PdfHash != null) Verify(Destination(pdfName), s.PdfHash);
        if (s.PdfHash != null) VerifyIdentity(Destination(pdfName), s.PdfIdentity!);
        token.ThrowIfCancellationRequested();
        DeleteVerified(xml, s.SourceXmlHash!);
        Checkpoint?.Invoke("XmlDeleted");
        if (s.PdfHash != null) DeleteVerified(pdf, s.PdfHash);
        if (s.PdfHash == null)
            logger.LogWarning("Обработано без PDF: {File}, requisitionID={RequisitionId}", s.Name, string.Join(",", s.RequisitionIds.Select(SafeId)));
        logger.LogInformation("Обработка завершена: {File}, замен {Count}, PDF={HasPdf}", s.Name, s.Replacements, s.PdfHash != null);
        // Журнал удаляем последним: Published и сохраненных хешей/идентификаторов
        // достаточно, чтобы завершить очистку даже после удаления каталога .staging.
        var dir = StageDirectory(s);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        File.Delete(StatePath(s.Name));
        observations.Remove(xml);
        observations.Remove(pdf);
    }

    /// <summary>Публикует один файл либо подтверждает собственную публикацию, прерванную сбоем.</summary>
    private void Publish(PairState s, string kind, string name, string hash, bool intent, Action setIntent)
    {
        var destination = Destination(name);
        if (File.Exists(destination))
        {
            // Совпадения байтов недостаточно: проверяются намерение и идентичность объекта NTFS.
            if (!intent) throw new ConflictException();
            Verify(destination, hash);
            VerifyIdentity(destination, kind == "xml" ? s.XmlIdentity! : s.PdfIdentity!);
            return;
        }
        var stage = Path.Combine(StageDirectory(s), kind);
        Verify(stage, hash);
        VerifyIdentity(stage, kind == "xml" ? s.XmlIdentity! : s.PdfIdentity!);
        // Сначала журнал, потом переименование: обратный порядок создал бы
        // окно, в котором наш файл после сбоя невозможно отличить от чужого.
        setIntent();
        Save(s);
        Checkpoint?.Invoke(kind + "Intent");
        // false запрещает перезапись, даже если конкурент создал файл после нашей проверки.
        File.Move(stage, destination, false);
    }

    /// <summary>Проверяет неизменность размера/времени и возможность чтения без параллельной записи.</summary>
    private bool Ready(string path, DateTimeOffset now)
    {
        var info = new FileInfo(path);
        if (!info.Exists) { observations.Remove(path); return false; }
        // Любое изменение начинает новый период стабильности, но не меняет FirstReady задания.
        var signature = (info.Length, info.LastWriteTimeUtc);
        if (!observations.TryGetValue(path, out var old) || old.Size != signature.Length || old.Write != signature.LastWriteTimeUtc)
        { observations[path] = (signature.Length, signature.LastWriteTimeUtc, now); return false; }
        if (now - old.Since < TimeSpan.FromSeconds(options.FileStabilitySeconds)) return false;
        try
        {
            using var stream = OpenSource(path);
            return stream.Length == old.Size && File.GetLastWriteTimeUtc(path) == old.Write;
        }
        catch (IOException) { logger.LogDebug("Ожидание завершения записи: {File}", Path.GetFileName(path)); return false; }
        catch (UnauthorizedAccessException) { logger.LogWarning("Ожидание доступа к файлу: {File}", Path.GetFileName(path)); return false; }
    }
    // FileShare.Read разрешает другим читателям доступ, но запрещает запись и удаление во время чтения.
    private static FileStream OpenSource(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    private static byte[] ReadAll(Stream stream) { using var buffer = new MemoryStream(); stream.CopyTo(buffer); return buffer.ToArray(); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Verify(string path, string hash)
    {
        using var stream = OpenSource(path);
        if (Convert.ToHexString(SHA256.HashData(stream)) != hash) throw new ConflictException();
    }
    private static void VerifyIdentity(string path, string expected)
    {
        if (FileIdentity.Read(path) != expected) throw new ConflictException();
    }
    private static void DeleteVerified(string path, string hash)
    {
        if (!File.Exists(path)) return;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Delete);
        if (Convert.ToHexString(SHA256.HashData(stream)) != hash) throw new ConflictException();
        File.Delete(path);
    }
    private string Destination(string name) => Path.Combine(options.OutputDirectory, name);
    private string StageDirectory(PairState s) => Path.Combine(options.OutputDirectory, ".staging", s.Transaction!);
    private string StatePath(string name) => Path.Combine(options.StateDirectory, Hash(System.Text.Encoding.UTF8.GetBytes(name.ToUpperInvariant())) + ".json");
    private void Save(PairState state)
    {
        var path = StatePath(state.Name);
        WriteDurable(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(state));
        File.Move(path + ".tmp", path, true);
    }
    private static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }
    private static string SafeId(string value) => new(value.Take(128).Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_').ToArray());
    public void Dispose() { instanceLock.Dispose(); gate.Dispose(); }
}

public sealed class ConflictException : IOException;
