using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using OpenAIUsageWidget.Core;
using OpenAIUsageWidget.Desktop;
using OpenAIUsageWidget.Presentation;

if (args.Contains("app-server"))
{
    await FakeCodex.RunAsync();
    return 0;
}

var tests = new List<(string Name, Func<Task> Run)>();
var now = new DateTimeOffset(2026, 10, 21, 21, 50, 0, TimeSpan.Zero);
void Test(string name, Action action) => tests.Add((name, () => { action(); return Task.CompletedTask; }));
void AsyncTest(string name, Func<Task> action) => tests.Add((name, action));
UsageSnapshot Parse(string json) { using var document = JsonDocument.Parse(json); return UsageParser.Parse(document.RootElement, now); }

Test("Los porcentajes son restantes, no consumidos", () =>
{
    var result = Parse(FakeCodex.Snapshot);
    Check.Equal(88d, result.FiveHour!.RemainingPercent);
    Check.Equal(74d, result.Weekly!.RemainingPercent);
    Check.Equal(3L, result.AvailableResets);
    Check.Equal(now, result.CapturedAt);
});
Test("El contador disponible es autoritativo, no la longitud de detalles", () =>
{
    var result = Parse("""{"rateLimits":{},"rateLimitResetCredits":{"availableCount":3,"credits":[]}}""");
    Check.Equal(3L, result.AvailableResets);
});
Test("Cero reseteos y saldo desconocido son distintos", () =>
{
    Check.Equal(0L, Parse("""{"rateLimits":{},"rateLimitResetCredits":{"availableCount":0}}""").AvailableResets);
    Check.True(Parse("""{"rateLimits":{},"rateLimitResetCredits":null}""").AvailableResets is null);
    Check.True(Parse("""{"rateLimits":{}}""").AvailableResets is null);
});
Test("Un límite semanal primario no se confunde con 5 horas", () =>
{
    var result = Parse("""{"rateLimits":{"primary":{"usedPercent":40,"windowDurationMins":10080}}}""");
    Check.True(result.FiveHour is null);
    Check.Equal(60d, result.Weekly!.RemainingPercent);
});
Test("Duraciones desconocidas no se etiquetan como 5 horas", () =>
{
    var result = Parse("""{"rateLimits":{"primary":{"usedPercent":20,"windowDurationMins":null}}}""");
    Check.True(result.FiveHour is null && result.Weekly is null);
});
Test("Se elige el bucket codex y no una cuota adicional", () =>
{
    var result = Parse("""{"rateLimits":{"primary":{"usedPercent":99,"windowDurationMins":300}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":20,"windowDurationMins":300}}}}""");
    Check.Equal(80d, result.FiveHour!.RemainingPercent);
});
Test("Una ventana ausente no se transforma en 100 por ciento", () =>
{
    var result = Parse("""{"rateLimits":{"primary":null,"secondary":null}}""");
    Check.True(result.FiveHour is null);
    Check.Equal("—", UsageFormatting.Percent(result.FiveHour));
});
Test("Se limitan los valores de las barras a 0..100", () =>
{
    Check.Equal(0d, new UsageWindow(120, 300, null).RemainingPercent);
    Check.Equal(100d, new UsageWindow(-20, 300, null).RemainingPercent);
});
Test("Porcentajes no numéricos se rechazan", () =>
    Check.Throws<InvalidDataException>(() => Parse("""{"rateLimits":{"primary":{"usedPercent":"bad","windowDurationMins":300}}}""")));
Test("Un contador negativo se rechaza", () =>
    Check.Throws<InvalidDataException>(() => Parse("""{"rateLimits":{},"rateLimitResetCredits":{"availableCount":-1}}""")));
Test("Se rechazan las respuestas sin límites", () => Check.Throws<InvalidDataException>(() => Parse("{}")));
Test("Una fecha fuera de rango se rechaza", () =>
    Check.Throws<InvalidDataException>(() => Parse("""{"rateLimits":{"primary":{"usedPercent":12,"resetsAt":9223372036854775807}}}""")));
Test("Una cuota de otra cuenta no se acepta", () =>
{
    var account = new AccountProfile { ServiceAccountId = "other-account" };
    Check.Throws<InvalidDataException>(() => UsageParser.ValidateAccount(account, Parse(FakeCodex.Snapshot)));
});
Test("No se reinicia artificialmente el porcentaje al llegar a la fecha", () =>
{
    var window = new UsageWindow(90, 300, now.AddMinutes(-1));
    Check.Equal("Pendiente de actualización", UsageFormatting.Reset(window, now));
    Check.Equal("10%", UsageFormatting.Percent(window));
});
Test("Se formatean las cuentas regresivas en español", () =>
{
    Check.Equal("Se restablece en 3 h 47 min", UsageFormatting.Reset(new UsageWindow(0, 300, now.AddHours(3).AddMinutes(47)), now));
    Check.Equal("Se restablece en 5 d 9 h", UsageFormatting.Reset(new UsageWindow(0, 10080, now.AddDays(5).AddHours(9)), now));
    Check.Equal("Se restablece en menos de 1 min", UsageFormatting.Reset(new UsageWindow(0, 300, now.AddSeconds(20)), now));
});
Test("Se formatean iniciales y planes desconocidos", () =>
{
    Check.Equal("CP", UsageFormatting.Initials(" Cuenta personal "));
    Check.Equal("?", UsageFormatting.Initials("  "));
    Check.Equal("Plan no disponible", UsageFormatting.Plan(null));
});
Test("Se bloquean generación, ejecución, compra y canje", () =>
{
    foreach (var method in new[] { "turn/start", "thread/start", "exec/command", "account/rateLimits/resetCredits/consume", "account/credits/purchase", "initialized", "ACCOUNT/READ" })
        Check.Throws<InvalidOperationException>(() => ReadOnlyRpcPolicy.DemandAllowed(method));
});
Test("Solo se permiten autenticación y lecturas de cuenta", () =>
{
    foreach (var method in new[] { "initialize", "account/read", "account/rateLimits/read", "account/login/start", "account/login/cancel", "account/logout" })
        ReadOnlyRpcPolicy.DemandAllowed(method);
});
Test("No se abren direcciones de login suplantadas", () =>
{
    Check.True(ReadOnlyRpcPolicy.IsSafeLoginUrl(new Uri("https://auth.openai.com/oauth/authorize")));
    foreach (var url in new[] { "http://auth.openai.com", "https://auth.openai.com.evil.invalid", "https://evil.invalid", "https://name@auth.openai.com", "https://auth.openai.com:1234", "file:///C:/test.exe" })
        Check.True(!ReadOnlyRpcPolicy.IsSafeLoginUrl(new Uri(url)));
});
Test("El inicio automático lee la entrada del instalador y cita rutas con espacios", () =>
{
    using var registry = new TestRegistryKey();
    Check.True(!StartupRegistration.IsEnabled(null));
    Check.True(!StartupRegistration.IsEnabled(registry.Key));
    const string executable = @"C:\Users\Usuario de prueba\AppData\Local\Programs\OpenAIUsageWidget\OpenAIUsageWidget.exe";
    registry.Key.SetValue("OpenAIUsageWidget", $"\"{executable}\""); // Same entry written by Inno Setup.
    Check.True(StartupRegistration.IsEnabled(registry.Key));
    StartupRegistration.SetEnabled(registry.Key, false, null);
    Check.True(!StartupRegistration.IsEnabled(registry.Key));
    StartupRegistration.SetEnabled(registry.Key, true, executable);
    Check.Equal($"\"{executable}\"", registry.Key.GetValue("OpenAIUsageWidget"));
    Check.Equal(RegistryValueKind.String, registry.Key.GetValueKind("OpenAIUsageWidget"));
});
Test("Desactivar inicio automático no borra otras entradas y es idempotente", () =>
{
    using var registry = new TestRegistryKey();
    registry.Key.SetValue("OtraAplicacion", "no tocar");
    StartupRegistration.SetEnabled(registry.Key, true, @"C:\Widget\OpenAIUsageWidget.exe");
    StartupRegistration.SetEnabled(registry.Key, false, null);
    StartupRegistration.SetEnabled(registry.Key, false, null);
    Check.True(!StartupRegistration.IsEnabled(registry.Key));
    Check.Equal("no tocar", registry.Key.GetValue("OtraAplicacion"));
    registry.Key.SetValue("OpenAIUsageWidget", "  ");
    Check.True(!StartupRegistration.IsEnabled(registry.Key));
});
Test("El inicio automático valida ejecutables y admite dotnet con rutas citadas", () =>
{
    using var registry = new TestRegistryKey();
    Check.Throws<InvalidOperationException>(() => StartupRegistration.SetEnabled(registry.Key, true, null));
    Check.Throws<InvalidOperationException>(() => StartupRegistration.SetEnabled(registry.Key, true, @"C:\Program Files\dotnet\dotnet.exe"));
    Check.True(!StartupRegistration.IsEnabled(registry.Key));
    StartupRegistration.SetEnabled(registry.Key, true, @"C:\Program Files\dotnet\dotnet.exe", @"C:\Mi Widget\OpenAIUsageWidget.dll");
    Check.Equal("\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\Mi Widget\\OpenAIUsageWidget.dll\"", registry.Key.GetValue("OpenAIUsageWidget"));
});
AsyncTest("Se persisten varias cuentas, selección, posición y última lectura", async () =>
{
    using var directory = new TestDirectory();
    var store = new SettingsStore(directory.Path);
    var first = new AccountProfile { DisplayName = "Personal", LastUsage = Parse(FakeCodex.Snapshot) };
    var second = new AccountProfile { DisplayName = "Trabajo" };
    await store.SaveAsync(new WidgetSettings { Accounts = [first, second], SelectedAccountId = second.Id, Left = -450, Top = 32 });
    var loaded = await store.LoadAsync();
    Check.Equal(2, loaded.Accounts.Count);
    Check.Equal(second.Id, loaded.SelectedAccountId);
    Check.Equal(-450d, loaded.Left);
    Check.Equal(3L, loaded.Accounts[0].LastUsage!.AvailableResets);
    Check.True(!File.Exists(store.FilePath + ".tmp"));
});
AsyncTest("Una configuración dañada no se sobrescribe", async () =>
{
    using var directory = new TestDirectory();
    var store = new SettingsStore(directory.Path);
    await File.WriteAllTextAsync(store.FilePath, "{broken");
    await Check.ThrowsAsync<InvalidDataException>(() => store.LoadAsync());
    Check.Equal("{broken", await File.ReadAllTextAsync(store.FilePath));
});
AsyncTest("Se rechazan versiones futuras y cuentas duplicadas", async () =>
{
    using var directory = new TestDirectory();
    var store = new SettingsStore(directory.Path);
    await Check.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new WidgetSettings { SchemaVersion = 2 }));
    var profile = new AccountProfile();
    await Check.ThrowsAsync<InvalidDataException>(() => store.SaveAsync(new WidgetSettings { Accounts = [profile, profile] }));
});
AsyncTest("Las escrituras simultáneas producen JSON completo", async () =>
{
    using var directory = new TestDirectory();
    var store = new SettingsStore(directory.Path);
    await Task.WhenAll(Enumerable.Range(0, 15).Select(i => store.SaveAsync(new WidgetSettings { Left = i })));
    Check.True((await store.LoadAsync()).Left is >= 0 and <= 14);
});
AsyncTest("Cada cuenta tiene un perfil aislado", async () =>
{
    using var directory = new TestDirectory();
    var store = new SettingsStore(directory.Path);
    Check.True(store.ProfileDirectory(Guid.NewGuid()) != store.ProfileDirectory(Guid.NewGuid()));
    Check.True((await store.LoadAsync()).Accounts.Count == 0);
});
AsyncTest("El transporte stdio lee cuotas y rechaza ejecución inversa", async () =>
{
    using var directory = new TestDirectory();
    Environment.SetEnvironmentVariable("OPENAI_API_KEY", "test-placeholder-not-a-secret");
    await using (var client = await CodexRpcClient.StartAsync(FakeCodex.Executable, directory.Path, CancellationToken.None))
    {
        var response = await client.CallAsync("account/rateLimits/read", new { excludeResetCreditDetails = true }, CancellationToken.None);
        Check.Equal(3L, UsageParser.Parse(response, now).AvailableResets);
        await Check.ThrowsAsync<InvalidOperationException>(() => client.CallAsync("turn/start", null, CancellationToken.None));
    }
    var trace = await File.ReadAllTextAsync(System.IO.Path.Combine(directory.Path, "trace.txt"));
    Check.True(trace.Contains("server-request-rejected"));
    Check.True(trace.Contains("no-inherited-api-key"));
    Check.True(!trace.Contains("turn/start"));
});
AsyncTest("El inicio de sesión usa un navegador y luego solo lecturas", async () =>
{
    using var directory = new TestDirectory();
    var store = new SettingsStore(directory.Path);
    var service = new CodexAccountService(store, () => FakeCodex.Executable);
    Uri? browser = null;
    var result = await service.LoginAsync(Guid.NewGuid(), uri => browser = uri, CancellationToken.None);
    Check.Equal("example@example.invalid", result.Email);
    Check.Equal(3L, result.Usage!.AvailableResets);
    Check.True(browser is not null && ReadOnlyRpcPolicy.IsSafeLoginUrl(browser));
});
AsyncTest("El servicio valida la identidad de una lectura", async () =>
{
    using var directory = new TestDirectory();
    var service = new CodexAccountService(new SettingsStore(directory.Path), () => FakeCodex.Executable);
    await Check.ThrowsAsync<InvalidDataException>(() => service.ReadUsageAsync(new AccountProfile { ServiceAccountId = "wrong" }, CancellationToken.None));
});
AsyncTest("La ausencia de conector tiene un error claro y no instala nada", async () =>
{
    using var directory = new TestDirectory();
    var service = new CodexAccountService(new SettingsStore(directory.Path), () => System.IO.Path.Combine(directory.Path, "missing.exe"));
    await Check.ThrowsAsync<FileNotFoundException>(() => service.ReadUsageAsync(new AccountProfile(), CancellationToken.None));
    Check.True(!Directory.EnumerateFileSystemEntries(directory.Path).Any());
});

Test("Sin cuentas, se ofrece agregar la primera", () =>
{
    using var directory = new TestDirectory();
    using var model = new WidgetViewModel(new SettingsStore(directory.Path), new WidgetSettings(), new StubUsageService());
    var requested = false;
    model.AddAccountRequested += () => requested = true;
    model.AddAccountCommand.Execute(null);
    Check.True(model.HasNoAccounts && !model.HasAccounts && requested);
    Check.True(!model.RefreshCommand.CanExecute(null));
});
AsyncTest("Se recupera la cuenta seleccionada y el refresco manual solo la consulta a ella", async () =>
{
    using var directory = new TestDirectory();
    var first = new AccountProfile { DisplayName = "Personal" };
    var second = new AccountProfile { DisplayName = "Trabajo" };
    var service = new StubUsageService();
    using var model = new WidgetViewModel(new SettingsStore(directory.Path), new WidgetSettings { Accounts = [first, second], SelectedAccountId = second.Id }, service);
    await model.RefreshSelectedAsync();
    Check.Equal(second.Id, model.SelectedAccount!.Id);
    Check.Equal(second.Id, service.ReadIds.Single());
    Check.Equal(3L, model.SelectedAccount.Profile.LastUsage!.AvailableResets);
});
AsyncTest("Un fallo conserva los porcentajes y la fecha del último éxito", async () =>
{
    using var directory = new TestDirectory();
    var original = Parse(FakeCodex.Snapshot);
    var account = new AccountProfile { LastUsage = original };
    var service = new StubUsageService { Read = (_, _) => throw new IOException("Simulated offline") };
    using var model = new WidgetViewModel(new SettingsStore(directory.Path), new WidgetSettings { Accounts = [account] }, service);
    await model.RefreshSelectedAsync();
    Check.Equal(original, model.SelectedAccount!.Profile.LastUsage);
    Check.True(model.SelectedAccount.Error is not null);
    Check.Equal(original.CapturedAt, model.SelectedAccount.Profile.LastUsage!.CapturedAt);
});
AsyncTest("No hay consultas superpuestas para una misma cuenta", async () =>
{
    using var directory = new TestDirectory();
    var pending = new TaskCompletionSource<UsageSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    var service = new StubUsageService { Read = (_, _) => pending.Task };
    using var model = new WidgetViewModel(new SettingsStore(directory.Path), new WidgetSettings { Accounts = [new AccountProfile()] }, service);
    var first = model.RefreshSelectedAsync();
    await model.RefreshSelectedAsync();
    Check.Equal(1, service.ReadIds.Count);
    Check.True(!model.RefreshCommand.CanExecute(null));
    pending.SetResult(Parse(FakeCodex.Snapshot));
    await first;
    Check.True(model.RefreshCommand.CanExecute(null));
});
AsyncTest("La reanudación solo consulta lecturas vencidas", async () =>
{
    using var directory = new TestDirectory();
    var snapshot = Parse(FakeCodex.Snapshot) with { CapturedAt = DateTimeOffset.UtcNow };
    var fresh = new AccountProfile { LastUsage = snapshot };
    var old = new AccountProfile { LastUsage = snapshot with { CapturedAt = DateTimeOffset.UtcNow.AddMinutes(-10) } };
    var service = new StubUsageService();
    using var model = new WidgetViewModel(new SettingsStore(directory.Path), new WidgetSettings { Accounts = [fresh, old] }, service);
    await model.RefreshAllAsync(onlyStale: true);
    Check.Equal(old.Id, service.ReadIds.Single());
});
AsyncTest("Conectar agrega, selecciona y persiste la cuenta", async () =>
{
    using var directory = new TestDirectory();
    var store = new SettingsStore(directory.Path);
    using var model = new WidgetViewModel(store, new WidgetSettings(), new StubUsageService());
    await model.ConnectAsync("Personal", _ => { }, CancellationToken.None);
    Check.Equal(1, model.Accounts.Count);
    Check.True(model.HasAccounts && model.SelectedAccount is not null);
    Check.Equal("Personal", (await store.LoadAsync()).Accounts.Single().DisplayName);
});
AsyncTest("No se duplica una cuenta ya agregada", async () =>
{
    using var directory = new TestDirectory();
    var service = new StubUsageService();
    var account = new AccountProfile { ServiceAccountId = "test-account" };
    using var model = new WidgetViewModel(new SettingsStore(directory.Path), new WidgetSettings { Accounts = [account] }, service);
    await Check.ThrowsAsync<InvalidOperationException>(() => model.ConnectAsync("Duplicada", _ => { }, CancellationToken.None));
    Check.Equal(1, model.Accounts.Count);
    Check.Equal(1, service.LogoutIds.Count);
    Check.True(service.LogoutIds.Single() != account.Id);
});
AsyncTest("Quitar la última cuenta restaura el estado inicial", async () =>
{
    using var directory = new TestDirectory();
    var account = new AccountProfile();
    var service = new StubUsageService();
    using var model = new WidgetViewModel(new SettingsStore(directory.Path), new WidgetSettings { Accounts = [account] }, service);
    await model.RemoveAsync(model.SelectedAccount!);
    Check.True(model.HasNoAccounts && model.SelectedAccount is null);
    Check.Equal(account.Id, service.LogoutIds.Single());
});
AsyncTest("La demostración no consulta ni persiste datos", async () =>
{
    using var directory = new TestDirectory();
    var service = new StubUsageService();
    using var model = new WidgetViewModel(new SettingsStore(directory.Path), new WidgetSettings { Accounts = [new AccountProfile()] }, service, demo: true);
    await model.RefreshAllAsync();
    await model.SaveAsync();
    Check.Equal(0, service.ReadIds.Count);
    Check.True(!File.Exists(System.IO.Path.Combine(directory.Path, "settings.json")));
});

var failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {exception}"); }
}
Console.WriteLine($"\n{tests.Count - failures}/{tests.Count} pruebas correctas.");
return failures == 0 ? 0 : 1;

static class Check
{
    public static void True(bool condition) { if (!condition) throw new Exception("La condición no se cumple."); }
    public static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Esperado: {expected}; recibido: {actual}"); }
    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"No se produjo {typeof(T).Name}.");
    }
    public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception($"No se produjo {typeof(T).Name}.");
    }
}

sealed class StubUsageService : IAccountUsageService
{
    public List<Guid> ReadIds { get; } = [];
    public List<Guid> LogoutIds { get; } = [];
    public Func<AccountProfile, CancellationToken, Task<UsageSnapshot>>? Read { get; init; }
    private static UsageSnapshot Snapshot()
    {
        using var document = JsonDocument.Parse(FakeCodex.Snapshot);
        return UsageParser.Parse(document.RootElement, DateTimeOffset.UtcNow);
    }
    public Task<UsageSnapshot> ReadUsageAsync(AccountProfile profile, CancellationToken cancellationToken)
    { ReadIds.Add(profile.Id); return Read?.Invoke(profile, cancellationToken) ?? Task.FromResult(Snapshot()); }
    public Task<LoginResult> LoginAsync(Guid profileId, Action<Uri> openBrowser, CancellationToken cancellationToken) =>
        Task.FromResult(new LoginResult("example@example.invalid", "plus", Snapshot()));
    public Task LogoutAsync(Guid profileId, CancellationToken cancellationToken)
    { LogoutIds.Add(profileId); return Task.CompletedTask; }
}

sealed class TestRegistryKey : IDisposable
{
    private readonly string _path = @"Software\OpenAIUsageWidget.Tests\" + Guid.NewGuid().ToString("N");
    public RegistryKey Key { get; }
    public TestRegistryKey() => Key = Registry.CurrentUser.CreateSubKey(_path);
    public void Dispose()
    {
        Key.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_path); // Only this test's fresh GUID key, never HKCU Run.
    }
}

sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Temp", "opencode", "openai-usage-tests-" + Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true); // Only this instance's fresh GUID directory.
}

static class FakeCodex
{
    public static string Executable => System.IO.Path.Combine(AppContext.BaseDirectory, "OpenAIUsageWidget.Tests.exe");
    public const string Snapshot = """
        {"accountId":"test-account","rateLimits":{"planType":"plus","primary":{"usedPercent":12,"windowDurationMins":300,"resetsAt":1792630800},"secondary":{"usedPercent":26,"windowDurationMins":10080,"resetsAt":1793070000}},"rateLimitResetCredits":{"availableCount":3,"credits":[]}}
        """;

    public static async Task RunAsync()
    {
        var directory = Environment.GetEnvironmentVariable("CODEX_HOME")!;
        var trace = System.IO.Path.Combine(directory, "trace.txt");
        await File.AppendAllTextAsync(trace, Environment.GetEnvironmentVariable("OPENAI_API_KEY") is null ? "no-inherited-api-key\n" : "inherited-api-key\n");
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (!root.TryGetProperty("method", out var method))
            {
                if (root.TryGetProperty("error", out var error) && error.GetProperty("code").GetInt32() == -32601)
                    await File.AppendAllTextAsync(trace, "server-request-rejected\n");
                continue;
            }
            var name = method.GetString()!;
            await File.AppendAllTextAsync(trace, name + "\n");
            if (!root.TryGetProperty("id", out var id)) continue;
            object result = name switch
            {
                "initialize" => new { userAgent = "test/1.0.0" },
                "account/rateLimits/read" => JsonSerializer.Deserialize<JsonElement>(Snapshot),
                "account/login/start" => new { type = "chatgpt", loginId = "test-login", authUrl = "https://auth.openai.com/oauth/authorize" },
                "account/read" => new { account = new { type = "chatgpt", email = "example@example.invalid", planType = "plus" } },
                _ => new { }
            };
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(new { id = id.Clone(), result }));
            if (name == "initialize")
                await Console.Out.WriteLineAsync("""{"id":"server-test","method":"exec/command","params":{}}""");
            if (name == "account/login/start")
                await Console.Out.WriteLineAsync("""{"method":"account/login/completed","params":{"loginId":"test-login","success":true}}""");
            await Console.Out.FlushAsync();
        }
    }
}
