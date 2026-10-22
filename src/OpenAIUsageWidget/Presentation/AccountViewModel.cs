namespace OpenAIUsageWidget.Presentation;

public sealed class AccountViewModel(AccountProfile profile) : ObservableObject
{
    public AccountProfile Profile { get; private set; } = profile;
    public Guid Id => Profile.Id;
    public string DisplayName => Profile.DisplayName;
    public string Initials => UsageFormatting.Initials(DisplayName);
    public string Email => Profile.Email ?? "";
    public string PlanLabel => UsageFormatting.Plan(Profile.LastUsage?.Plan ?? Profile.Plan);
    public string PlanColor => (Profile.LastUsage?.Plan ?? Profile.Plan) == "free" ? "#747B87" : "#00E6A0";
    public string AvatarColor => (Id.ToByteArray()[0] % 4) switch
    { 0 => "#A73CEC", 1 => "#DA5B98", 2 => "#397DED", _ => "#BF8531" };
    public string FiveHourPercent => UsageFormatting.Percent(Profile.LastUsage?.FiveHour);
    public string WeeklyPercent => UsageFormatting.Percent(Profile.LastUsage?.Weekly);
    public double FiveHourRemaining => Profile.LastUsage?.FiveHour?.RemainingPercent ?? 0;
    public double WeeklyRemaining => Profile.LastUsage?.Weekly?.RemainingPercent ?? 0;
    public string FiveHourReset => UsageFormatting.Reset(Profile.LastUsage?.FiveHour, DateTimeOffset.UtcNow);
    public string WeeklyReset => UsageFormatting.Reset(Profile.LastUsage?.Weekly, DateTimeOffset.UtcNow);
    public string FiveHourDetail => Profile.LastUsage?.FiveHour?.ResetsAt?.ToLocalTime().ToString("f") ?? "Dato no disponible";
    public string WeeklyDetail => Profile.LastUsage?.Weekly?.ResetsAt?.ToLocalTime().ToString("f") ?? "Dato no disponible";
    public string LastUpdate => UsageFormatting.LastUpdate(Profile.LastUsage);
    public string AvailableResets => Profile.LastUsage?.AvailableResets?.ToString() ?? "—";
    public string ResetsDetail => Profile.LastUsage?.AvailableResets is null
        ? "OpenAI no devolvió el saldo de restablecimientos. No equivale a cero."
        : "Restablecimientos otorgados por OpenAI. Este widget no permite utilizarlos.";
    public bool HasSnapshot => Profile.LastUsage is not null;
    public bool IsStale => Profile.LastUsage is { } snapshot && DateTimeOffset.UtcNow - snapshot.CapturedAt > TimeSpan.FromMinutes(6);
    public bool HasWarning => Error is not null || IsStale;
    public string WarningText => Error is not null
        ? (HasSnapshot ? "Sin actualizar · se conserva la última lectura" : "No se pudo consultar la cuenta")
        : "Datos desactualizados";

    private bool _refreshing;
    public bool IsRefreshing { get => _refreshing; set { _refreshing = value; Changed(); } }
    private bool _selected;
    public bool IsSelected { get => _selected; set { _selected = value; Changed(); } }
    public string? Error { get; private set; }

    public void SetProfile(AccountProfile profile) { Profile = profile; Changed(null); }
    public void SetError(string? message) { Error = message; Changed(null); }
    public void Tick() => Changed(null);
}
