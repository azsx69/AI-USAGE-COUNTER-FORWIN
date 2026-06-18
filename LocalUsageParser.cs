using System.Text.Json;

namespace AiUsageCounter;

// Offline estimate from Claude Code's local JSONL logs (~/.claude/projects).
// Trimmed port of the macOS UsageParser: builds 5-hour billing blocks, detects
// the plan ceiling from rate_limit error events, and totals the current week.
public static class LocalUsageParser
{
    private readonly struct Record
    {
        public Record(DateTime ts, int tokens, bool isRateLimit, bool isExtraUsage)
        { Timestamp = ts; Tokens = tokens; IsRateLimit = isRateLimit; IsExtraUsage = isExtraUsage; }
        public DateTime Timestamp { get; }
        public int Tokens { get; }
        public bool IsRateLimit { get; }
        public bool IsExtraUsage { get; }
    }

    public static string ProjectsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static Task<LocalUsage> ParseAsync() => Task.Run(Parse);

    private static LocalUsage Parse()
    {
        var result = new LocalUsage();
        string dir = ProjectsDir;
        if (!Directory.Exists(dir)) return result;

        var records = new List<Record>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl", SearchOption.AllDirectories))
            CollectRecords(file, records);

        if (records.Count == 0) return result;
        records.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));

        // 5-hour billing blocks (boundary = blockStart + 5h, matching ccusage).
        var windowDuration = TimeSpan.FromHours(5);
        var blocks = new List<(DateTime start, DateTime last, int tokens)>();
        (DateTime start, DateTime last, int tokens)? cur = null;
        foreach (var r in records)
        {
            if (cur is { } c && r.Timestamp < c.start + windowDuration)
                cur = (c.start, r.Timestamp, c.tokens + r.Tokens);
            else
            {
                if (cur is { } prev) blocks.Add(prev);
                var hourStart = new DateTime(r.Timestamp.Year, r.Timestamp.Month, r.Timestamp.Day, r.Timestamp.Hour, 0, 0, r.Timestamp.Kind);
                cur = (hourStart, r.Timestamp, r.Tokens);
            }
        }
        if (cur is { } lastBlock) blocks.Add(lastBlock);

        var (sessionLimit, weeklyExtra) = DetectLimits(records, windowDuration);
        int detectedSession = sessionLimit > 0 ? sessionLimit : (blocks.Count > 0 ? blocks.Max(b => b.tokens) : 1);
        result.SessionLimit = Math.Max(1, detectedSession);

        // Current active block: activity within the last 5h and before its reset.
        var now = DateTime.Now;
        if (blocks.Count > 0)
        {
            var b = blocks[^1];
            var reset = b.start + windowDuration;
            bool active = reset > now && (now - b.last) < windowDuration;
            if (active)
            {
                result.SessionActive = true;
                result.SessionTokens = b.tokens;
                result.SessionResetAt = reset;
            }
        }

        // Current week (Mon-Sun).
        var weekStart = StartOfWeek(now);
        int weeklyTokens = records.Where(r => r.Timestamp >= weekStart && !r.IsRateLimit && !r.IsExtraUsage)
                                  .Sum(r => r.Tokens);
        result.WeeklyTokens = weeklyTokens;
        result.WeeklyLimit = weeklyExtra > 0 ? weeklyExtra : Math.Max(1, MaxWeeklyTokens(records));

        return result;
    }

    private static (int session, int weekly) DetectLimits(List<Record> sorted, TimeSpan window)
    {
        var sessionCandidates = new List<int>();
        var weeklyCandidates = new List<int>();
        (DateTime start, int tokens)? cur = null;

        foreach (var r in sorted)
        {
            if (cur == null) cur = (r.Timestamp, 0);
            else if (r.Timestamp >= cur.Value.start + window) cur = (r.Timestamp, 0);

            if (r.IsRateLimit) sessionCandidates.Add(cur.Value.tokens);
            else if (r.IsExtraUsage) weeklyCandidates.Add(cur.Value.tokens);
            else cur = (cur.Value.start, cur.Value.tokens + r.Tokens);
        }

        int rawSession = sessionCandidates.Count > 0 ? sessionCandidates.Max() : 0;
        var filtered = sessionCandidates.Where(v => v >= rawSession / 2).ToList();
        int session = filtered.Count > 0 ? filtered[^1] : rawSession;
        int weekly = weeklyCandidates.Count > 0 ? weeklyCandidates[^1] : 0;
        return (session, weekly);
    }

    private static int MaxWeeklyTokens(List<Record> sorted)
    {
        var groups = new Dictionary<DateTime, int>();
        foreach (var r in sorted)
        {
            var ws = StartOfWeek(r.Timestamp);
            groups[ws] = groups.GetValueOrDefault(ws) + r.Tokens;
        }
        return groups.Count > 0 ? groups.Values.Max() : 1;
    }

    private static DateTime StartOfWeek(DateTime d)
    {
        var date = d.Date;
        int diff = (7 + (int)date.DayOfWeek - (int)DayOfWeek.Monday) % 7;
        return date.AddDays(-diff);
    }

    private static void CollectRecords(string path, List<Record> into)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return; }

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var rec = ParseRecord(line);
            if (rec != null) into.Add(rec.Value);
        }
    }

    private static Record? ParseRecord(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var obj = doc.RootElement;
            if (obj.ValueKind != JsonValueKind.Object) return null;
            if (!obj.TryGetProperty("type", out var t) || t.GetString() != "assistant") return null;
            if (!obj.TryGetProperty("timestamp", out var tsEl)) return null;
            if (!DateTimeOffset.TryParse(tsEl.GetString(), out var dto)) return null;
            if (!obj.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) return null;
            if (!msg.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;

            int tokens = GetInt(usage, "input_tokens") + GetInt(usage, "output_tokens")
                       + GetInt(usage, "cache_creation_input_tokens") + GetInt(usage, "cache_read_input_tokens");

            string errorStr = obj.TryGetProperty("error", out var e) ? (e.GetString() ?? "") : "";
            string msgText = FirstText(msg);
            bool isRateLimit = errorStr == "rate_limit" && msgText.Contains("hit your limit");
            bool isExtraUsage = errorStr == "rate_limit" && msgText.Contains("out of extra usage");

            return new Record(dto.LocalDateTime, tokens, isRateLimit, isExtraUsage);
        }
        catch { return null; }
    }

    private static int GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static string FirstText(JsonElement msg)
    {
        if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            && content.GetArrayLength() > 0)
        {
            var first = content[0];
            if (first.ValueKind == JsonValueKind.Object && first.TryGetProperty("text", out var txt))
                return txt.GetString() ?? "";
        }
        return "";
    }
}
