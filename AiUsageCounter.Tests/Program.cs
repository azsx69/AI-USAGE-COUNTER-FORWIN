using AiUsageCounter;

var tests = new (string Name, Func<bool> Run)[]
{
    ("Codex shows Session (5h) when session usage is available",
        () => PopupModel.ShouldShowSession("codex", 1.0)),
    ("Codex hides an empty session row when only weekly usage is available",
        () => !PopupModel.ShouldShowSession("codex", null)),
    ("Other providers retain their session row while usage is loading",
        () => PopupModel.ShouldShowSession("claude", null)),
    ("Codex converts 99% remaining to 1% used",
        () => CodexProvider.ToUsedPercent(99.0, remaining: true) == 1.0),
    ("Codex keeps an explicitly used percentage unchanged",
        () => CodexProvider.ToUsedPercent(7.0, remaining: false) == 7.0),
    ("Local parser totals tokens across log files", () => WithLogDir(dir =>
    {
        File.WriteAllText(Path.Combine(dir, "a.jsonl"), LogLine(100));
        File.WriteAllText(Path.Combine(dir, "b.jsonl"), LogLine(50));
        return LocalUsageParser.Parse(dir).WeeklyTokens == 150;
    })),
    ("Local parser re-reads a log file after it grows", () => WithLogDir(dir =>
    {
        var a = Path.Combine(dir, "a.jsonl");
        File.WriteAllText(a, LogLine(100));
        LocalUsageParser.Parse(dir);
        File.AppendAllText(a, LogLine(25));
        return LocalUsageParser.Parse(dir).WeeklyTokens == 125;
    })),
    ("Local parser reuses cached records for an unchanged log file", () => WithLogDir(dir =>
    {
        var a = Path.Combine(dir, "a.jsonl");
        File.WriteAllText(a, LogLine(100));
        var stamp = File.GetLastWriteTimeUtc(a);
        LocalUsageParser.Parse(dir);
        // ขนาดเท่าเดิม + เวลาแก้ไขเท่าเดิม → ต้องได้ค่าจาก cache ไม่ใช่อ่านจาก disk
        File.WriteAllText(a, LogLine(900));
        File.SetLastWriteTimeUtc(a, stamp);
        return LocalUsageParser.Parse(dir).WeeklyTokens == 100;
    })),
    ("Local parser drops a deleted log file", () => WithLogDir(dir =>
    {
        File.WriteAllText(Path.Combine(dir, "a.jsonl"), LogLine(100));
        File.WriteAllText(Path.Combine(dir, "b.jsonl"), LogLine(50));
        LocalUsageParser.Parse(dir);
        File.Delete(Path.Combine(dir, "b.jsonl"));
        return LocalUsageParser.Parse(dir).WeeklyTokens == 100;
    })),
};

var failed = 0;
foreach (var test in tests)
{
    if (test.Run())
    {
        Console.WriteLine($"PASS {test.Name}");
        continue;
    }

    failed++;
    Console.Error.WriteLine($"FAIL {test.Name}");
}

return failed == 0 ? 0 : 1;

// โฟลเดอร์ log ชั่วคราวสำหรับ test — ลบทิ้งเมื่อจบ
static bool WithLogDir(Func<string, bool> body)
{
    var dir = Path.Combine(Path.GetTempPath(), "aiusage-test-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try { return body(dir); }
    finally { Directory.Delete(dir, recursive: true); }
}

// บรรทัด assistant ของ Claude Code แบบย่อ (timestamp = ตอนนี้ จึงอยู่ในสัปดาห์ปัจจุบันเสมอ)
static string LogLine(int tokens) =>
    "{\"type\":\"assistant\",\"timestamp\":\"" + DateTime.UtcNow.ToString("o")
    + "\",\"message\":{\"usage\":{\"input_tokens\":" + tokens + "}}}\n";
