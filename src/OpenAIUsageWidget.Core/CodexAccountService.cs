using System.Text.Json;

namespace OpenAIUsageWidget.Core;

public sealed class CodexAccountService(SettingsStore store, Func<string?> configuredExecutable, TimeProvider? clock = null) : IAccountUsageService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private Task<CodexRpcClient> ConnectAsync(Guid id, CancellationToken cancellationToken)
    {
        var executable = CodexExecutable.Find(configuredExecutable());
        if (executable is null)
            throw new FileNotFoundException("Para conectar una cuenta hace falta el conector oficial Codex CLI. " +
                "No es el agente de desarrollo. Podés indicar su ubicación en Configuración; no se instala automáticamente.");
        return CodexRpcClient.StartAsync(executable, store.ProfileDirectory(id), cancellationToken);
    }

    public async Task<LoginResult> LoginAsync(Guid profileId, Action<Uri> openBrowser, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(4));
        await using var client = await ConnectAsync(profileId, timeout.Token);
        var completed = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Notification += (method, data) =>
        {
            if (method == "account/login/completed") completed.TrySetResult(data);
        };
        var login = await client.CallAsync("account/login/start", new { type = "chatgpt" }, timeout.Token);
        var url = UsageParser.GetString(login, "authUrl");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !ReadOnlyRpcPolicy.IsSafeLoginUrl(uri))
            throw new InvalidDataException("Codex no devolvió una dirección de inicio de sesión válida de OpenAI.");
        openBrowser(uri);
        var outcome = await completed.Task.WaitAsync(timeout.Token);
        if (!outcome.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("No se completó el inicio de sesión. Volvé a intentarlo.");
        var response = await client.CallAsync("account/read", new { refreshToken = false }, timeout.Token);
        if (!response.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object ||
            UsageParser.GetString(account, "type") != "chatgpt")
            throw new InvalidDataException("La sesión no corresponde a una cuenta de ChatGPT.");

        UsageSnapshot? snapshot = null;
        try { snapshot = await ReadAsync(client, timeout.Token); }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or TimeoutException) { }
        return new LoginResult(UsageParser.GetString(account, "email"), UsageParser.GetString(account, "planType"), snapshot);
    }

    public async Task<UsageSnapshot> ReadUsageAsync(AccountProfile profile, CancellationToken cancellationToken)
    {
        await using var client = await ConnectAsync(profile.Id, cancellationToken);
        var snapshot = await ReadAsync(client, cancellationToken);
        UsageParser.ValidateAccount(profile, snapshot);
        return snapshot;
    }

    private async Task<UsageSnapshot> ReadAsync(CodexRpcClient client, CancellationToken cancellationToken)
    {
        // The summary count is returned without a separate detailed reset-credit lookup.
        var result = await client.CallAsync("account/rateLimits/read", new { excludeResetCreditDetails = true }, cancellationToken);
        return UsageParser.Parse(result, _clock.GetUtcNow());
    }

    public async Task LogoutAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await using var client = await ConnectAsync(profileId, cancellationToken);
        await client.CallAsync("account/logout", null, cancellationToken);
    }
}
