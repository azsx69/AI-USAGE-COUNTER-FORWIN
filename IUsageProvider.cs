namespace AiUsageCounter;

// Common surface for every provider (Claude, Codex, …). Mirrors the macOS
// UsageProvider protocol so the tray can drive them uniformly.
public interface IUsageProvider
{
    string Id { get; }
    string DisplayName { get; }

    Task<AuthState> CheckAuthAsync();
    Task<bool> PresentLoginAsync();
    Task SignOutAsync();
    Task<FetchResult> FetchUsageAsync();
}

// Tiny persisted flag store (JSON in %APPDATA%). Used for sticky login hints
// where cookie names shift across provider auth revisions.
public static class AppState
{
    private static readonly string Path = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AiUsageCounter", "state.json");

    private static Dictionary<string, bool> _flags = Load();

    private static Dictionary<string, bool> Load()
    {
        try
        {
            if (File.Exists(Path))
                return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(Path))
                       ?? new();
        }
        catch { /* ignore corrupt state */ }
        return new();
    }

    public static bool GetBool(string key) => _flags.TryGetValue(key, out var v) && v;

    public static void SetBool(string key, bool value)
    {
        _flags[key] = value;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, System.Text.Json.JsonSerializer.Serialize(_flags));
        }
        catch { /* best effort */ }
    }
}
