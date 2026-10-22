namespace OpenAIUsageWidget.Core;

public sealed record UsageWindow(double UsedPercent, int? DurationMinutes, DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
}

public sealed record UsageSnapshot(
    string? AccountId,
    string? Plan,
    UsageWindow? FiveHour,
    UsageWindow? Weekly,
    long? AvailableResets,
    DateTimeOffset CapturedAt);

public sealed record AccountProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string DisplayName { get; init; } = "Mi cuenta";
    public string? Email { get; init; }
    public string? ServiceAccountId { get; init; }
    public string? Plan { get; init; }
    public UsageSnapshot? LastUsage { get; init; }
}

public sealed record WidgetSettings
{
    public int SchemaVersion { get; init; } = 1;
    public List<AccountProfile> Accounts { get; init; } = [];
    public Guid? SelectedAccountId { get; init; }
    public double? Left { get; init; }
    public double? Top { get; init; }
    public string? CodexExecutablePath { get; init; }
    public bool StartWithWindows { get; init; }
}

public sealed record LoginResult(string? Email, string? Plan, UsageSnapshot? Usage);

public interface IAccountUsageService
{
    Task<LoginResult> LoginAsync(Guid profileId, Action<Uri> openBrowser, CancellationToken cancellationToken);
    Task<UsageSnapshot> ReadUsageAsync(AccountProfile profile, CancellationToken cancellationToken);
    Task LogoutAsync(Guid profileId, CancellationToken cancellationToken);
}
