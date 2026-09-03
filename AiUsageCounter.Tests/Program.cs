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
