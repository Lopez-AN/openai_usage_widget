using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace OpenAIUsageWidget.Core;

/// <summary>A private, short-lived stdio connection. It cannot call model or redemption methods.</summary>
public sealed class CodexRpcClient : IAsyncDisposable
{
    private static readonly ConcurrentDictionary<int, Process> OwnedProcesses = new();
    private readonly Process _process;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Task _reader;
    private readonly Task _errorDrain;
    private long _nextId;
    private bool _disposed;
    public event Action<string, JsonElement>? Notification;

    private CodexRpcClient(Process process)
    {
        _process = process;
        OwnedProcesses[process.Id] = process;
        _reader = ReadLoopAsync();
        _errorDrain = DrainErrorsAsync();
    }

    public static async Task<CodexRpcClient> StartAsync(string executable, string profileDirectory, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(profileDirectory);
        var start = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = profileDirectory
        };
        const string arguments = "app-server -c cli_auth_credentials_store=\"keyring\"";
        if (Path.GetExtension(executable).Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            // Only an existing, validated executable path is accepted; there are no user-supplied arguments.
            if (executable.IndexOfAny(['"', '\r', '\n', '%']) >= 0)
                throw new InvalidOperationException("Usá la ruta del codex.exe nativo para esta ubicación.");
            start.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            start.Arguments = $"/d /s /c \"\"{executable}\" {arguments}\"";
        }
        else
        {
            start.ArgumentList.Add("app-server");
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("cli_auth_credentials_store=\"keyring\"");
        }

        start.Environment["CODEX_HOME"] = profileDirectory;
        // Never inherit unrelated API credentials or a developer's alternate provider configuration.
        foreach (var key in new[] { "OPENAI_API_KEY", "OPENAI_BASE_URL", "OPENAI_ORG_ID", "OPENAI_ORGANIZATION",
                     "OPENAI_PROJECT_ID", "CODEX_API_KEY", "CODEX_CONFIG", "CODEX_CONFIG_TOML" })
            start.Environment.Remove(key);

        var process = Process.Start(start) ?? throw new IOException("No se pudo iniciar el conector de Codex.");
        var client = new CodexRpcClient(process);
        try
        {
            await client.CallAsync("initialize", new
            {
                clientInfo = new { name = "openai_usage_widget", title = "OpenAI Usage Widget", version = "0.1.0" },
                capabilities = new { experimentalApi = false }
            }, cancellationToken);
            await client.WriteAsync(new { method = "initialized" }, cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public async Task<JsonElement> CallAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        ReadOnlyRpcPolicy.DemandAllowed(method);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteAsync(new { id, method, @params = parameters }, cancellationToken);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(40), cancellationToken);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    private async Task WriteAsync(object message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message);
        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            await _process.StandardInput.WriteLineAsync(json.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
        }
        finally { _sendLock.Release(); }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(_shutdown.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var document = JsonDocument.Parse(line);
                var message = document.RootElement;
                if (message.TryGetProperty("method", out var method))
                {
                    if (message.TryGetProperty("id", out var requestId))
                    {
                        // Reject all server-initiated actions: this client has no tool/command execution surface.
                        await WriteAsync(new { id = requestId.Clone(), error = new { code = -32601, message = "Read-only client: unsupported request" } }, _shutdown.Token);
                    }
                    else if (message.TryGetProperty("params", out var data))
                        Notification?.Invoke(method.GetString() ?? "", data.Clone());
                }
                else if (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number &&
                         id.TryGetInt64(out var number) && _pending.TryGetValue(number, out var completion))
                {
                    if (message.TryGetProperty("error", out _))
                        completion.TrySetException(new InvalidOperationException(
                            "Codex no pudo consultar la cuenta. Comprobá la sesión y que Codex CLI esté actualizado."));
                    else if (message.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
                }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
            FailPending(new IOException("Se interrumpió la comunicación con Codex.", exception));
        }
        finally { FailPending(new IOException("El conector de Codex se cerró.")); }
    }

    private async Task DrainErrorsAsync()
    {
        // Drain, but do not log stderr: it can contain authentication URLs or other sensitive data.
        try { while (await _process.StandardError.ReadLineAsync(_shutdown.Token) is not null) { } }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (IOException) { }
    }

    private void FailPending(Exception exception)
    {
        foreach (var request in _pending.Values) request.TrySetException(exception);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _shutdown.CancelAsync();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        try { await Task.WhenAll(_reader, _errorDrain).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { }
        OwnedProcesses.TryRemove(_process.Id, out _);
        _process.Dispose();
        // The reader can still unwind after a timeout; leave its cancellation and send gates alive.
    }

    public static void StopOwnedProcesses()
    {
        foreach (var process in OwnedProcesses.Values)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
}
