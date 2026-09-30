using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace InternalIntegration;

public sealed class PairState
{
    public string Name { get; set; } = "";
    public DateTimeOffset FirstReady { get; set; }
    public bool PdfSeen { get; set; }
    public string? PdfName { get; set; }
    public string? Transaction { get; set; }
    public string? SourceXmlHash { get; set; }
    public string? XmlHash { get; set; }
    public string? PdfHash { get; set; }
    public string? XmlIdentity { get; set; }
    public string? PdfIdentity { get; set; }
    public string[] RequisitionIds { get; set; } = [];
    public int Replacements { get; set; }
    public bool PdfIntent { get; set; }
    public bool XmlIntent { get; set; }
    public bool Published { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset RetryAfter { get; set; }
    public bool Blocked { get; set; }
}

public sealed class PairEngine : IDisposable
{
    private readonly ProcessingOptions options;
    private readonly ILogger logger;
    private readonly FileStream instanceLock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, (long Size, DateTime Write, DateTimeOffset Since)> observations = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> corruptStates = new(StringComparer.OrdinalIgnoreCase);
    // Test seam: simulate abrupt interruption at durable boundaries.
    public Action<string>? Checkpoint { get; set; }

    public PairEngine(ProcessingOptions options, ILogger logger)
    {
        this.options = options;
        this.logger = logger;
        // One processor per input, including instances configured with another State directory.
        instanceLock = new FileStream(Path.Combine(options.InputDirectory, ".processor.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    public async Task ScanAsync(DateTimeOffset now, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var states = new Dictionary<string, PairState>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in Directory.EnumerateFiles(options.StateDirectory, "*.json"))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var state = JsonSerializer.Deserialize<PairState>(File.ReadAllBytes(path)) ?? throw new JsonException();
                    if (state.Name != Path.GetFileName(state.Name) || string.IsNullOrWhiteSpace(state.Name) ||
                        Path.GetFullPath(path) != Path.GetFullPath(StatePath(state.Name)) ||
                        (state.PdfName != null && state.PdfName != Path.GetFileName(state.PdfName)) ||
                        (state.Transaction != null && !Guid.TryParseExact(state.Transaction, "N", out _))) throw new JsonException();
                    states.Add(state.Name, state);
                }
                catch (Exception e) when (e is JsonException or ArgumentException)
                {
                    // Do not process any input with an untrustworthy recovery journal.
                    if (corruptStates.Add(path)) logger.LogError("Поврежден журнал состояния {File}; обработка приостановлена", Path.GetFileName(path));
                    return;
                }
            }
            foreach (var xml in Directory.EnumerateFiles(options.InputDirectory).Where(p => Path.GetExtension(p).Equals(".xml", StringComparison.OrdinalIgnoreCase)))
            {
                // Original spelling of extensions is preserved; base name is the pair key.
                var name = Path.GetFileName(xml);
                if (!states.ContainsKey(name))
                {
                    try
                    {
                        if (!Ready(xml, now)) continue;
                        var state = new PairState { Name = name, FirstReady = now };
                        Save(state);
                        states.Add(name, state);
                    }
                    catch (IOException) { logger.LogWarning("XML пока недоступен: {File}", name); }
                    catch (UnauthorizedAccessException) { logger.LogWarning("Нет доступа к XML: {File}", name); }
                }
            }
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
                    // Exception messages can contain patient XML or other sensitive values: log only type.
                    if (state.Blocked) logger.LogError("Окончательная ошибка {Type}, файл {File}; исходники сохранены, попыток {Attempts}", e.GetType().Name, state.Name, state.Failures);
                    else logger.LogWarning("Временная ошибка {Type}, файл {File}, попытка {Attempt}", e.GetType().Name, state.Name, state.Failures);
                }
            }
        }
        finally { gate.Release(); }
    }

    private void Process(PairState s, DateTimeOffset now, CancellationToken token)
    {
        var xml = Path.Combine(options.InputDirectory, s.Name);
        var pdfName = s.PdfName ?? Path.ChangeExtension(s.Name, ".pdf");
        // Use actual PDF filename, including extension casing.
        var pdf = Directory.EnumerateFiles(options.InputDirectory).FirstOrDefault(p => Path.GetFileName(p).Equals(pdfName, StringComparison.OrdinalIgnoreCase))
            ?? Path.Combine(options.InputDirectory, pdfName);
        if (s.Transaction == null)
        {
            if (File.Exists(pdf) && !s.PdfSeen) { s.PdfSeen = true; s.PdfName = Path.GetFileName(pdf); pdfName = s.PdfName; Save(s); }
            if (!Ready(xml, now)) return;
            if (s.PdfSeen)
            {
                if (!Ready(pdf, now)) return; // Arrival before timeout is latched even while locked.
            }
            else if (now - s.FirstReady < TimeSpan.FromSeconds(options.PdfWaitTimeoutSeconds)) return;
            token.ThrowIfCancellationRequested();
            using var xmlStream = OpenSource(xml);
            using var pdfStream = s.PdfSeen ? OpenSource(pdf) : null;
            var bytes = ReadAll(xmlStream);
            var result = new XmlTransformer().Transform(bytes);
            logger.LogInformation("Начало обработки {File}; замен {Count}", s.Name, result.Replacements);
            if (result.AmbiguousTests > 0) logger.LogWarning("Неоднозначная принадлежность к отделению: {File}, тестов {Count}; эти тесты не изменены", s.Name, result.AmbiguousTests);
            // Check every destination before preparing the durable transaction.
            if (File.Exists(Destination(s.Name)) || File.Exists(Destination(pdfName))) throw new ConflictException();
            var id = Guid.NewGuid().ToString("N");
            var staging = Path.Combine(options.OutputDirectory, ".staging", id);
            Directory.CreateDirectory(staging);
            WriteDurable(Path.Combine(staging, "xml"), result.Bytes);
            string? pdfHash = null;
            if (pdfStream != null)
            {
                using var output = new FileStream(Path.Combine(staging, "pdf"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                pdfStream.CopyTo(output);
                output.Flush(true);
                pdfStream.Position = 0;
                pdfHash = Convert.ToHexString(SHA256.HashData(pdfStream));
            }
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
        if (!s.Published)
        {
            if (s.PdfHash != null) Publish(s, "pdf", pdfName, s.PdfHash, s.PdfIntent, () => s.PdfIntent = true);
            Checkpoint?.Invoke("AfterPdf");
            Publish(s, "xml", s.Name, s.XmlHash!, s.XmlIntent, () => s.XmlIntent = true);
            s.Published = true;
            Save(s);
            Checkpoint?.Invoke("Published");
        }
        // Revalidate both outputs before deleting any source, including after restart.
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
        // Staging is removed first; Published journal needs only hashes to finish recovery.
        var dir = StageDirectory(s);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        File.Delete(StatePath(s.Name));
        observations.Remove(xml);
        observations.Remove(pdf);
    }

    private void Publish(PairState s, string kind, string name, string hash, bool intent, Action setIntent)
    {
        var destination = Destination(name);
        if (File.Exists(destination))
        {
            if (!intent) throw new ConflictException();
            Verify(destination, hash);
            VerifyIdentity(destination, kind == "xml" ? s.XmlIdentity! : s.PdfIdentity!);
            return;
        }
        var stage = Path.Combine(StageDirectory(s), kind);
        Verify(stage, hash);
        VerifyIdentity(stage, kind == "xml" ? s.XmlIdentity! : s.PdfIdentity!);
        setIntent();
        Save(s);
        Checkpoint?.Invoke(kind + "Intent");
        // Same-volume rename; no overwrite. Journal intent and expected digest identify recovery.
        File.Move(stage, destination, false);
    }

    private bool Ready(string path, DateTimeOffset now)
    {
        var info = new FileInfo(path);
        if (!info.Exists) { observations.Remove(path); return false; }
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
