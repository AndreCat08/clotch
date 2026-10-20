using System.Text.Json;

namespace Clotch.Core.Local;

public sealed record TokenRecord(string Key, DateTimeOffset Timestamp, string Model, long Input, long Output, long CacheCreate, long CacheRead);

public sealed record ModelTokens(string Model, long Input, long Output, long CacheCreate, long CacheRead, int Messages)
{
    public long Total => Input + Output + CacheCreate + CacheRead;
}

public sealed record LocalUsageSummary(
    IReadOnlyList<ModelTokens> Today,
    IReadOnlyList<ModelTokens> Window,
    DateTimeOffset WindowStart,
    int FilesTracked)
{
    public long TodayTotal => Today.Sum(m => m.Total);
    public long WindowTotal => Window.Sum(m => m.Total);
}

/// <summary>
/// Incrementally aggregates token usage from Claude Code transcripts (~/.claude/projects/**/*.jsonl).
/// Only complete lines are consumed, entries are de-duplicated by message id + request id
/// (Claude Code writes one line per content block with identical usage), and records older than
/// the retention window are dropped. This is informational only — limit percentages come from the server.
/// </summary>
public sealed class LocalUsageScanner(string projectsDir, TimeProvider time, TimeSpan? retention = null)
{
    private static readonly byte[] AssistantMarker = "\"type\":\"assistant\""u8.ToArray();
    private static readonly byte[] UsageMarker = "\"usage\""u8.ToArray();

    private readonly TimeSpan _retention = retention ?? TimeSpan.FromDays(8);
    private readonly Dictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TokenRecord> _records = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public LocalUsageSummary Scan(DateTimeOffset? windowStart = null)
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            var cutoff = now - _retention;

            if (Directory.Exists(projectsDir))
            {
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(projectsDir, "*.jsonl", SearchOption.AllDirectories).ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { files = []; }

                foreach (var f in files)
                {
                    try
                    {
                        if (File.GetLastWriteTimeUtc(f) < cutoff.UtcDateTime) continue;
                        ReadNew(f, cutoff);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // File locked or vanished; try again next scan.
                    }
                }
            }

            foreach (var k in _records.Where(r => r.Value.Timestamp < cutoff).Select(r => r.Key).ToList())
                _records.Remove(k);

            var tz = time.LocalTimeZone;
            var today = TimeZoneInfo.ConvertTime(now, tz).Date;
            var ws = windowStart ?? now.AddHours(-5);

            return new LocalUsageSummary(
                Aggregate(_records.Values.Where(r => TimeZoneInfo.ConvertTime(r.Timestamp, tz).Date == today)),
                Aggregate(_records.Values.Where(r => r.Timestamp >= ws && r.Timestamp <= now)),
                ws,
                _offsets.Count);
        }
    }

    private void ReadNew(string path, DateTimeOffset cutoff)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1, FileOptions.SequentialScan);
        _offsets.TryGetValue(path, out var offset);
        if (fs.Length < offset) offset = 0; // truncated/rewritten: rescan, dedupe keeps totals right
        if (fs.Length == offset) return;
        fs.Seek(offset, SeekOrigin.Begin);

        // Stream in chunks so multi-megabyte transcripts never sit in memory whole.
        var buffer = new byte[128 * 1024];
        var carry = 0;
        var consumed = offset;
        while (true)
        {
            if (carry == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2); // one very long line
            var n = fs.Read(buffer, carry, buffer.Length - carry);
            if (n == 0) break;

            var len = carry + n;
            var start = 0;
            while (start < len)
            {
                var nl = Array.IndexOf(buffer, (byte)'\n', start, len - start);
                if (nl < 0) break;
                var line = buffer.AsSpan(start, nl - start);
                if (line.IndexOf(AssistantMarker) >= 0 && line.IndexOf(UsageMarker) >= 0 &&
                    TryParse(line, out var rec) && rec.Timestamp >= cutoff)
                    _records[rec.Key] = rec;
                consumed += nl - start + 1;
                start = nl + 1;
            }

            carry = len - start;
            if (carry > 0) Buffer.BlockCopy(buffer, start, buffer, 0, carry);
        }

        // A trailing partial line (still being written) stays unconsumed until it is completed.
        _offsets[path] = consumed;
    }

    internal static bool TryParse(ReadOnlySpan<byte> line, out TokenRecord record)
    {
        record = null!;
        try
        {
            var reader = new Utf8JsonReader(line);
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "assistant") return false;
            if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return false;
            if (!msg.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("timestamp", out var ts) || !ts.TryGetDateTimeOffset(out var when)) return false;

            var model = msg.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "unknown";
            if (model == "<synthetic>") return false;

            var msgId = msg.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            var reqId = root.TryGetProperty("requestId", out var rq) && rq.ValueKind == JsonValueKind.String ? rq.GetString() : null;
            var uuid = root.TryGetProperty("uuid", out var uu) && uu.ValueKind == JsonValueKind.String ? uu.GetString() : null;
            var key = msgId is null && reqId is null ? $"uuid:{uuid}" : $"{msgId}|{reqId}";
            if (key == "uuid:") return false;

            record = new TokenRecord(key, when, model,
                L(u, "input_tokens"), L(u, "output_tokens"),
                L(u, "cache_creation_input_tokens"), L(u, "cache_read_input_tokens"));
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static long L(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    private static List<ModelTokens> Aggregate(IEnumerable<TokenRecord> records) =>
        records.GroupBy(r => r.Model)
            .Select(g => new ModelTokens(g.Key, g.Sum(r => r.Input), g.Sum(r => r.Output),
                g.Sum(r => r.CacheCreate), g.Sum(r => r.CacheRead), g.Count()))
            .OrderByDescending(m => m.Total)
            .ToList();
}
