namespace Codex.RuntimeKit.AppServer.Usage;

/// <summary>
/// A normalized bucket of rate limits (e.g. limitId "codex").
/// </summary>
public sealed record LimitBucket(
    string LimitId,
    string? LimitName,
    IReadOnlyList<UsageWindow> Windows,
    string? RateLimitReachedType,
    string? PlanType);
