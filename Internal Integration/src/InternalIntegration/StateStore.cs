using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InternalIntegration;

/// <summary>Один JSON на задание: дедлайн PDF и контрольные данные незавершенной публикации.</summary>
public sealed class PairState
{
    public string Name { get; set; } = "";
    public DateTimeOffset FirstReady { get; set; }
    public bool PdfSeen { get; set; }
    public string? PdfName { get; set; }
    // null: ожидаем файлы; GUID: временные результаты подготовлены.
    public string? Transaction { get; set; }
    public string? SourceXmlHash { get; set; }
    public string? XmlHash { get; set; }
    public string? PdfHash { get; set; }
    public string? XmlIdentity { get; set; }
    public string? PdfIdentity { get; set; }
    public string[] RequisitionIds { get; set; } = [];
    public RequisitionResult[] Results { get; set; } = [];
    public int Replacements { get; set; }
    // Намерение пишется ДО переименования, Published - ПОСЛЕ публикации обоих результатов.
    public bool PdfIntent { get; set; }
    public bool XmlIntent { get; set; }
    public bool Published { get; set; }
    public int Failures { get; set; }
    public DateTimeOffset RetryAfter { get; set; }
    public bool Blocked { get; set; }
}

/// <summary>Изолирует чтение/атомарное обновление состояния от обработки XML и мониторинга.</summary>
public sealed class StateStore(string directory)
{
    public Dictionary<string, PairState> Load()
    {
        var states = new Dictionary<string, PairState>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            var state = JsonSerializer.Deserialize<PairState>(File.ReadAllBytes(path)) ?? throw new JsonException();
            // Совместимый формат сохраняет возможность продолжить задания прежней версии.
            // Не допускаем путей вне рабочих папок через подмененные поля JSON.
            if (!IsName(state.Name) || Path.GetFullPath(path) != Path.GetFullPath(PathFor(state.Name)) ||
                (state.PdfName != null && !IsName(state.PdfName)) ||
                (state.Transaction != null && !Guid.TryParseExact(state.Transaction, "N", out _)) ||
                state.RequisitionIds == null || state.Results == null || !states.TryAdd(state.Name, state))
                throw new JsonException("Некорректное состояние задания.");
        }
        return states;
    }

    public void Save(PairState state)
    {
        var path = PathFor(state.Name);
        // Старый JSON остается пригодным до готовности нового. Временный файл не загружаем.
        WriteDurable(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(state));
        File.Move(path + ".tmp", path, true);
    }

    public void Delete(PairState state) => File.Delete(PathFor(state.Name));

    public static void WriteDurable(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(true);
    }

    private string PathFor(string name) => Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.ToUpperInvariant()))) + ".json");
    private static bool IsName(string name) => !string.IsNullOrWhiteSpace(name) &&
        name == Path.GetFileName(name) && name is not "." and not "..";
}
