namespace AiUsageCounter;

// Normalized usage snapshot. SessionPct / WeeklyPct are always "percent USED" (0-100).
public sealed class ProviderUsage
{
    public double? SessionPct { get; set; }
    public double? WeeklyPct { get; set; }
    public DateTime? SessionResetAt { get; set; }
    public DateTime? WeeklyResetAt { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.Now;

    public bool SessionAtLimit => (SessionPct ?? 0) >= 99.99;
    public bool WeeklyAtLimit => (WeeklyPct ?? 0) >= 99.99;
}

public enum AuthState { SignedOut, SignedIn, Expired }

public enum FetchKind { Success, AuthExpired, Failure }

public sealed class FetchResult
{
    public FetchKind Kind { get; init; }
    public ProviderUsage? Usage { get; init; }

    public static FetchResult Success(ProviderUsage u) => new() { Kind = FetchKind.Success, Usage = u };
    public static readonly FetchResult AuthExpired = new() { Kind = FetchKind.AuthExpired };
    public static readonly FetchResult Failure = new() { Kind = FetchKind.Failure };
}

// Local .jsonl estimate (offline fallback, mirrors the macOS app's Claude Code reader).
public sealed class LocalUsage
{
    public int SessionTokens { get; set; }
    public int SessionLimit { get; set; } = 1;
    public DateTime? SessionResetAt { get; set; }
    public bool SessionActive { get; set; }

    public int WeeklyTokens { get; set; }
    public int WeeklyLimit { get; set; } = 1;

    public double SessionPct => SessionActive ? Math.Min(1.0, (double)SessionTokens / Math.Max(1, SessionLimit)) * 100 : 0;
    public double WeeklyPct => Math.Min(1.0, (double)WeeklyTokens / Math.Max(1, WeeklyLimit)) * 100;
}
