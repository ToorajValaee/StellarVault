using System.Text;
using System.Text.Json;

namespace StellarVault.Core.Services;

/// <summary>
/// Portable, plaintext interchange for ordinary records and folders only.
/// Deliberately not a vault backup, ciphertext export or transaction journal.
/// Callers must obtain explicit plaintext-export consent and save to a
/// user-selected destination. Imports only stage encrypted local drafts.
/// </summary>
public static class VaultRecordInterchange
{
    public const string Format = "stellar-vault-records";
    public const int Version = 1;
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumRecords = 500;

    public sealed record Entry(string Id, string Title, string Content,
        string? FolderId, string[] Tags, bool IsFolder,
        string? UserName, string? Url, string? Notes);
    public sealed record Archive(string Format, int Version, Entry[] Records);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    public static bool IsOrdinary(LedgerRecordItem item) =>
        item.Content is not null && IsOrdinaryContent(item.Content);

    public static bool IsOrdinaryContent(string content) =>
        !content.StartsWith(VaultLinkedTreeFilePolicy.Prefix, StringComparison.Ordinal) &&
        !content.StartsWith(StellarLedgerFileProtocol.SinglePrefix, StringComparison.Ordinal) &&
        !content.StartsWith(StellarLedgerFileProtocol.ManifestPrefix, StringComparison.Ordinal) &&
        !content.StartsWith(StellarLedgerFileProtocol.ChunkPrefix, StringComparison.Ordinal);

    public static Archive FromPublished(IEnumerable<LedgerRecordItem> published)
    {
        ArgumentNullException.ThrowIfNull(published);
        var entries = published.Where(IsOrdinary).Select(x =>
            new Entry(x.Id, x.Title, x.Content, x.FolderId,
                x.Tags?.ToArray() ?? [], x.IsFolder, x.UserName, x.Url, x.Notes)).ToArray();
        return Validate(new Archive(Format, Version, entries));
    }

    public static byte[] ExportJson(Archive archive)
    {
        Validate(archive);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(archive, JsonOptions);
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("Record export exceeds the 1 MiB interchange limit.");
        return bytes;
    }

    public static Archive ImportJson(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumBytes)
            throw new InvalidDataException("Invalid record archive size.");
        try
        {
            return Validate(JsonSerializer.Deserialize<Archive>(bytes, JsonOptions)
                ?? throw new InvalidDataException("Empty record archive."));
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Invalid versioned record JSON.", ex);
        }
    }

    public static Archive ImportCsv(string csv)
    {
        ArgumentNullException.ThrowIfNull(csv);
        if (Encoding.UTF8.GetByteCount(csv) > MaximumBytes)
            throw new InvalidDataException("CSV record import exceeds 1 MiB.");
        var rows = ParseCsv(csv);
        if (rows.Count == 0) throw new InvalidDataException("CSV header is missing.");
        // Excel and password managers sometimes include a UTF-8 BOM.
        if (rows[0].Count > 0)
            rows[0][0] = rows[0][0].TrimStart('\uFEFF');
        var header = rows[0];
        var expected = new[] { "title", "content", "username", "url", "notes", "tags" };
        if (!header.SequenceEqual(expected, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "CSV header must be title,content,username,url,notes,tags.");
        var entries = rows.Skip(1).Where(x => x.Any(y => y.Length > 0)).Select(x =>
        {
            if (x.Count != expected.Length)
                throw new InvalidDataException("CSV record has an unexpected column count.");
            return new Entry(Guid.NewGuid().ToString("N"), x[0], x[1], null,
                x[5].Length == 0 ? [] : x[5].Split(';', StringSplitOptions.TrimEntries),
                false, x[2], x[3], x[4]);
        }).ToArray();
        return Validate(new Archive(Format, Version, entries));
    }

    public static byte[] ExportCsv(Archive archive)
    {
        Validate(archive);
        if (archive.Records.Any(x => x.IsFolder || x.FolderId is not null))
            throw new NotSupportedException(
                "CSV cannot preserve folders. Use versioned JSON for folder hierarchies.");
        var sb = new StringBuilder("title,content,username,url,notes,tags\r\n");
        foreach (var x in archive.Records)
        {
            if (x.Tags.Any(tag => tag.Contains(';')))
                throw new NotSupportedException(
                    "CSV cannot preserve semicolons inside tags. Export JSON instead.");
            sb.Append(string.Join(",", new[] { x.Title, x.Content, x.UserName ?? "",
                x.Url ?? "", x.Notes ?? "", string.Join(";", x.Tags) }.Select(CsvCell)));
            sb.Append("\r\n");
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        if (bytes.Length > MaximumBytes)
            throw new InvalidDataException("CSV export exceeds 1 MiB.");
        return bytes;
    }

    private static string CsvCell(string x)
    {
        // Spreadsheet applications sometimes execute values beginning with
        // formula operators. CSV export refuses such values rather than
        // silently modifying credentials or emitting an executable formula.
        if (x.TrimStart().StartsWith('=') || x.TrimStart().StartsWith('+') ||
            x.TrimStart().StartsWith('-') || x.TrimStart().StartsWith('@'))
            throw new InvalidDataException(
                "CSV contains a spreadsheet formula-like field. Export JSON instead.");
        return "\"" + x.Replace("\"", "\"\"") + "\"";
    }

    private static List<List<string>> ParseCsv(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false, closed = false;
        for (int i = 0; i < csv.Length; i++)
        {
            var ch = csv[i];
            if (quoted)
            {
                if (ch == '"' && i + 1 < csv.Length && csv[i + 1] == '"')
                { cell.Append('"'); i++; }
                else if (ch == '"') { quoted = false; closed = true; }
                else cell.Append(ch);
            }
            else if (ch == '"' && !closed && cell.Length == 0) quoted = true;
            else if (ch == ',')
            {
                row.Add(cell.ToString()); cell.Clear(); closed = false;
            }
            else if (ch == '\r' || ch == '\n')
            {
                if (ch == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear(); closed = false;
                rows.Add(row); row = new List<string>();
                if (rows.Count > MaximumRecords + 2)
                    throw new InvalidDataException("CSV record count limit exceeded.");
            }
            else if (closed || ch == '"')
                throw new InvalidDataException("Malformed quoted CSV field.");
            else cell.Append(ch);
        }
        if (quoted) throw new InvalidDataException("Unterminated CSV field.");
        if (cell.Length != 0 || row.Count != 0 || closed)
        { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    public static Archive Validate(Archive archive)
    {
        ArgumentNullException.ThrowIfNull(archive);
        if (archive.Format != Format || archive.Version != Version ||
            archive.Records is null || archive.Records.Length > MaximumRecords)
            throw new InvalidDataException("Unsupported record archive format or size.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var folders = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var item in archive.Records)
        {
            if (item is null || item.Id is not { Length: > 0 and <= 128 } ||
                !ids.Add(item.Id) ||
                string.IsNullOrWhiteSpace(item.Title) || item.Title.Length > 256 ||
                string.IsNullOrEmpty(item.Content) || item.Content.Length > 65536 ||
                !IsOrdinaryContent(item.Content) ||
                item.Tags is null || item.Tags.Length > 24 ||
                item.Tags.Any(x => x is null || x.Length > 64) ||
                item.FolderId?.Length > 128 ||
                item.UserName?.Length > 4096 || item.Url?.Length > 4096 ||
                item.Notes?.Length > 65536 || item.IsFolder && item.FolderId == item.Id)
                throw new InvalidDataException("Invalid or duplicate portable record.");
            if (item.IsFolder) folders.Add(item.Id, item);
        }
        foreach (var item in archive.Records)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var parent = item.FolderId;
            while (parent is not null)
            {
                if (!visited.Add(parent) ||
                    !folders.TryGetValue(parent, out var folder))
                    throw new InvalidDataException(
                        "Imported folder reference is missing, cyclic or not a folder.");
                parent = folder.FolderId;
            }
        }
        return archive;
    }

    /// <summary>
    /// Stage children after their parents have been durably encrypted.
    /// The callback MUST be an existing consent-gated local queue CreateAsync;
    /// never a publisher or network API. A failure leaves only pending drafts.
    /// </summary>
    public static async Task<int> StageAsync(Archive archive,
        Func<LedgerRecordDraft, CancellationToken, Task<string>> stageLocal,
        CancellationToken ct = default)
    {
        Validate(archive);
        ArgumentNullException.ThrowIfNull(stageLocal);
        var remaining = new List<Entry>(archive.Records);
        var translated = new Dictionary<string, string>(StringComparer.Ordinal);
        var createdRecordIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        while (remaining.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var ready = remaining.FirstOrDefault(x =>
                x.FolderId is null || translated.ContainsKey(x.FolderId));
            if (ready is null)
                throw new InvalidDataException("Unresolvable imported folder hierarchy.");
            var parentId = ready.FolderId is null ? null : translated[ready.FolderId];
            var draft = new LedgerRecordDraft(ready.Title, ready.Content, parentId,
                ready.Tags.ToArray(), ready.IsFolder, ready.UserName, ready.Url, ready.Notes);
            var newId = await stageLocal(draft, ct);
            if (newId is not { Length: 16 } || !newId.All(Uri.IsHexDigit) ||
                !createdRecordIds.Add(newId))
                throw new InvalidDataException(
                    "Encrypted draft staging returned an invalid or duplicate record ID.");
            translated.Add(ready.Id, newId);
            remaining.Remove(ready);
            count++;
        }
        return count;
    }
}
