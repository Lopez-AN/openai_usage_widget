namespace OpenAIUsageWidget.Core;

/// <summary>Authentication and read-only account RPC only. No model, tools, redemption or purchases.</summary>
public static class ReadOnlyRpcPolicy
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "initialize", "account/read", "account/rateLimits/read",
        "account/login/start", "account/login/cancel", "account/logout"
    };

    public static void DemandAllowed(string method)
    {
        if (!Allowed.Contains(method))
            throw new InvalidOperationException($"Operación bloqueada por la política de solo lectura: {method}");
    }

    public static bool IsSafeLoginUrl(Uri uri) => uri.Scheme == Uri.UriSchemeHttps &&
        uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
        (uri.Host.Equals("auth.openai.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase));
}
