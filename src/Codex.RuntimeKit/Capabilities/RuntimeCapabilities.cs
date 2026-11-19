namespace Codex.RuntimeKit.Capabilities;

/// <summary>Capability observations for one Codex executable identity.</summary>
public sealed record RuntimeCapabilities(
    CapabilityStatus AccountRead = CapabilityStatus.Supported,
    CapabilityStatus RateLimitsRead = CapabilityStatus.Supported,
    CapabilityStatus AccountUsageRead = CapabilityStatus.Unknown,
    CapabilityStatus RateLimitNotifications = CapabilityStatus.Unknown,
    CapabilityStatus DetailedResetCredits = CapabilityStatus.Unknown,
    CapabilityStatus RateLimitsModernParams = CapabilityStatus.Unknown)
{
    public bool CanMonitorQuota =>
        AccountRead != CapabilityStatus.Unsupported &&
        RateLimitsRead != CapabilityStatus.Unsupported;

    public bool SupportsAccountUsage => AccountUsageRead == CapabilityStatus.Supported;

    public static RuntimeCapabilities Default => new();

    public static RuntimeCapabilities LegacyUnsupportedUsage { get; } = new(
        AccountRead: CapabilityStatus.Supported,
        RateLimitsRead: CapabilityStatus.Supported,
        AccountUsageRead: CapabilityStatus.Unsupported,
        RateLimitNotifications: CapabilityStatus.Unsupported,
        DetailedResetCredits: CapabilityStatus.Unsupported,
        RateLimitsModernParams: CapabilityStatus.Unsupported);

    public static RuntimeCapabilities ModernFull { get; } = new(
        AccountRead: CapabilityStatus.Supported,
        RateLimitsRead: CapabilityStatus.Supported,
        AccountUsageRead: CapabilityStatus.Supported,
        RateLimitNotifications: CapabilityStatus.Supported,
        DetailedResetCredits: CapabilityStatus.Supported,
        RateLimitsModernParams: CapabilityStatus.Supported);
}