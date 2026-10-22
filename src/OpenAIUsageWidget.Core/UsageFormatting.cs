using System.Globalization;

namespace OpenAIUsageWidget.Core;

public static class UsageFormatting
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-AR");

    public static string Percent(UsageWindow? window) => window is null
        ? "—" : $"{Math.Floor(window.RemainingPercent).ToString("0", Spanish)}%";

    public static string Reset(UsageWindow? window, DateTimeOffset now)
    {
        if (window?.ResetsAt is not { } resetsAt) return "Restablecimiento no disponible";
        var remaining = resetsAt - now;
        if (remaining <= TimeSpan.Zero) return "Pendiente de actualización";
        if (remaining.TotalMinutes < 1) return "Se restablece en menos de 1 min";
        var days = (int)remaining.TotalDays;
        var hours = remaining.Hours;
        var minutes = remaining.Minutes;
        var text = days > 0 ? $"{days} d {hours} h"
            : hours > 0 ? $"{hours} h {minutes} min" : $"{minutes} min";
        return $"Se restablece en {text}";
    }

    public static string LastUpdate(UsageSnapshot? snapshot) => snapshot is null
        ? "Todavía sin actualizar" : snapshot.CapturedAt.ToLocalTime().ToString("dd MMM yyyy, HH:mm", Spanish);

    public static string Plan(string? plan) => plan?.ToLowerInvariant() switch
    {
        "free" => "Free", "go" => "Go", "plus" => "Plus", "pro" => "Pro",
        "prolite" => "Pro Lite", "promax" => "Pro Max", "team" or "business" => "Business",
        "enterprise" => "Enterprise", "edu" => "Edu", _ => "Plan no disponible"
    };

    public static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return "?";
        var first = StringInfo.GetNextTextElement(words[0]);
        var last = words.Length > 1 ? StringInfo.GetNextTextElement(words[^1]) : "";
        return (first + last).ToUpperInvariant();
    }
}
