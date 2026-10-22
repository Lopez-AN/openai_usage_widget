using System.Text.Json;

namespace OpenAIUsageWidget.Core;

/// <summary>Maps Codex app-server snapshots, never inferring missing quota or reset credits.</summary>
public static class UsageParser
{
    public static UsageSnapshot Parse(JsonElement response, DateTimeOffset capturedAt)
    {
        if (response.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("La respuesta de cuotas no es válida.");
        JsonElement limits;
        if (response.TryGetProperty("rateLimitsByLimitId", out var buckets) &&
            buckets.ValueKind == JsonValueKind.Object && buckets.TryGetProperty("codex", out var codex))
        {
            limits = codex;
        }
        else if (!response.TryGetProperty("rateLimits", out limits) || limits.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("La respuesta no contiene los límites de la cuenta.");
        }

        if (limits.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Los límites recibidos no son válidos.");
        UsageWindow? fiveHour = null;
        UsageWindow? weekly = null;
        foreach (var property in new[] { "primary", "secondary" })
        {
            if (!limits.TryGetProperty(property, out var window) || window.ValueKind != JsonValueKind.Object)
                continue;
            if (!window.TryGetProperty("usedPercent", out var percent) || percent.ValueKind != JsonValueKind.Number ||
                !percent.TryGetDouble(out var used) || !double.IsFinite(used))
                throw new InvalidDataException("El porcentaje de uso recibido no es válido.");

            int? duration = window.TryGetProperty("windowDurationMins", out var minutes) && minutes.ValueKind == JsonValueKind.Number && minutes.TryGetInt32(out var m)
                ? m : null;
            DateTimeOffset? resetsAt = null;
            if (window.TryGetProperty("resetsAt", out var reset) && reset.ValueKind != JsonValueKind.Null)
            {
                if (reset.ValueKind != JsonValueKind.Number || !reset.TryGetInt64(out var timestamp))
                    throw new InvalidDataException("La fecha de restablecimiento recibida no es válida.");
                try { resetsAt = DateTimeOffset.FromUnixTimeSeconds(timestamp); }
                catch (ArgumentOutOfRangeException exception)
                { throw new InvalidDataException("La fecha de restablecimiento está fuera de rango.", exception); }
            }

            var parsed = new UsageWindow(used, duration, resetsAt);
            // A weekly bucket can be primary. Do not assume primary always means five hours.
            if (duration == 300) fiveHour = parsed;
            else if (duration == 10080) weekly = parsed;
        }

        long? resets = null;
        if (response.TryGetProperty("rateLimitResetCredits", out var credits) && credits.ValueKind == JsonValueKind.Object &&
            credits.TryGetProperty("availableCount", out var count) && count.ValueKind != JsonValueKind.Null)
        {
            if (count.ValueKind != JsonValueKind.Number || !count.TryGetInt64(out var available) || available < 0)
                throw new InvalidDataException("El saldo de restablecimientos recibido no es válido.");
            resets = available;
        }

        return new UsageSnapshot(GetString(response, "accountId"), GetString(limits, "planType"),
            fiveHour, weekly, resets, capturedAt);
    }

    public static void ValidateAccount(AccountProfile account, UsageSnapshot snapshot)
    {
        if (account.ServiceAccountId is { Length: > 0 } expected && snapshot.AccountId is { Length: > 0 } actual &&
            !string.Equals(expected, actual, StringComparison.Ordinal))
            throw new InvalidDataException("La respuesta pertenece a otra cuenta. Volvé a conectar esta cuenta.");
    }

    public static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
