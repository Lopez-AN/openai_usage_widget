using System.Collections.ObjectModel;

namespace OpenAIUsageWidget.Presentation;

public sealed class WidgetViewModel : ObservableObject, IDisposable
{
    private readonly SettingsStore _store;
    private readonly IAccountUsageService _usage;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _loginGate = new(1, 1);
    private WidgetSettings _settings;
    public ObservableCollection<AccountViewModel> Accounts { get; } = [];
    public bool IsDemo { get; }
    public bool HasAccounts => Accounts.Count > 0;
    public bool HasNoAccounts => !HasAccounts;
    public WidgetSettings Settings => _settings;
    public string StorageDirectory => _store.DirectoryPath;
    public CancellationToken Lifetime => _lifetime.Token;
    public DelegateCommand SelectAccountCommand { get; }
    public DelegateCommand RefreshCommand { get; }
    public DelegateCommand AddAccountCommand { get; }
    public DelegateCommand SettingsCommand { get; }
    public event Action? AddAccountRequested;
    public event Action? SettingsRequested;

    private AccountViewModel? _selectedAccount;
    public AccountViewModel? SelectedAccount
    {
        get => _selectedAccount;
        private set
        {
            if (_selectedAccount == value) return;
            if (_selectedAccount is not null) _selectedAccount.IsSelected = false;
            _selectedAccount = value;
            if (value is not null) value.IsSelected = true;
            Changed();
            RefreshCommand.Refresh();
        }
    }

    private string? _storageError;
    public string? StorageError { get => _storageError; private set { _storageError = value; Changed(); Changed(nameof(HasStorageError)); } }
    public bool HasStorageError => StorageError is not null;

    public WidgetViewModel(SettingsStore store, WidgetSettings settings, IAccountUsageService usage, bool demo = false)
    {
        _store = store;
        _settings = settings;
        _usage = usage;
        IsDemo = demo;
        SelectAccountCommand = new DelegateCommand(p => { if (p is AccountViewModel account) _ = SelectAsync(account); });
        RefreshCommand = new DelegateCommand(_ => _ = RefreshSelectedAsync(), _ => !IsDemo && SelectedAccount is { IsRefreshing: false });
        AddAccountCommand = new DelegateCommand(_ => AddAccountRequested?.Invoke(), _ => !IsDemo);
        SettingsCommand = new DelegateCommand(_ => SettingsRequested?.Invoke(), _ => !IsDemo);
        foreach (var account in settings.Accounts) Accounts.Add(new AccountViewModel(account));
        SelectedAccount = Accounts.FirstOrDefault(a => a.Id == settings.SelectedAccountId) ?? Accounts.FirstOrDefault();
    }

    public async Task SelectAsync(AccountViewModel account)
    {
        if (!Accounts.Contains(account)) return;
        SelectedAccount = account;
        await SaveAsync();
        if (account.Profile.LastUsage is null || DateTimeOffset.UtcNow - account.Profile.LastUsage.CapturedAt >= TimeSpan.FromMinutes(5))
            await RefreshAccountAsync(account);
    }

    public Task RefreshSelectedAsync() => SelectedAccount is { } account ? RefreshAccountAsync(account) : Task.CompletedTask;

    public async Task RefreshAllAsync(bool onlyStale = false)
    {
        if (IsDemo) return;
        foreach (var account in Accounts.ToArray())
        {
            if (_lifetime.IsCancellationRequested) break;
            if (!onlyStale || account.Profile.LastUsage is null ||
                DateTimeOffset.UtcNow - account.Profile.LastUsage.CapturedAt >= TimeSpan.FromMinutes(5))
                await RefreshAccountAsync(account);
        }
    }

    private async Task RefreshAccountAsync(AccountViewModel account)
    {
        if (IsDemo || account.IsRefreshing || !Accounts.Contains(account) || _lifetime.IsCancellationRequested) return;
        account.IsRefreshing = true;
        RefreshCommand.Refresh();
        try
        {
            var snapshot = await _usage.ReadUsageAsync(account.Profile, _lifetime.Token);
            if (!Accounts.Contains(account)) return;
            account.SetProfile(account.Profile with
            {
                LastUsage = snapshot, Plan = snapshot.Plan ?? account.Profile.Plan,
                ServiceAccountId = snapshot.AccountId ?? account.Profile.ServiceAccountId
            });
            account.SetError(null);
            await SaveAsync();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { account.SetError(SafeError(exception)); }
        finally { account.IsRefreshing = false; RefreshCommand.Refresh(); }
    }

    public async Task ConnectAsync(string name, Action<Uri> openBrowser, CancellationToken cancellationToken, Guid? reconnectId = null)
    {
        if (IsDemo) return;
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Escribí un nombre para esta cuenta.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        await _loginGate.WaitAsync(linked.Token);
        AccountViewModel? reconnecting = null;
        try
        {
            var id = reconnectId ?? Guid.NewGuid();
            reconnecting = Accounts.FirstOrDefault(a => a.Id == id);
            if (reconnecting?.IsRefreshing == true)
            { reconnecting = null; throw new InvalidOperationException("Esperá a que termine el refresco de esta cuenta."); }
            if (reconnecting is not null) reconnecting.IsRefreshing = true;
            RefreshCommand.Refresh();
            var result = await _usage.LoginAsync(id, openBrowser, linked.Token);
            var duplicate = Accounts.FirstOrDefault(a => a.Id != id &&
                (result.Usage?.AccountId is { } accountId && a.Profile.ServiceAccountId == accountId ||
                 result.Usage?.AccountId is null && result.Email is not null && string.Equals(a.Email, result.Email, StringComparison.OrdinalIgnoreCase)));
            if (duplicate is not null)
            {
                try { await _usage.LogoutAsync(id, _lifetime.Token); } catch { /* Only the newly created widget profile is involved. */ }
                throw new InvalidOperationException("Esta cuenta ya está agregada al widget.");
            }
            var existing = Accounts.FirstOrDefault(a => a.Id == id);
            var profile = new AccountProfile
            {
                Id = id, DisplayName = name.Trim(), Email = result.Email, Plan = result.Plan,
                ServiceAccountId = result.Usage?.AccountId ?? existing?.Profile.ServiceAccountId,
                LastUsage = result.Usage ?? existing?.Profile.LastUsage
            };
            if (existing is null) { existing = new AccountViewModel(profile); Accounts.Add(existing); }
            else { existing.SetProfile(profile); existing.SetError(null); }
            if (result.Usage is null) existing.SetError("La cuenta se conectó, pero aún no se pudieron consultar las cuotas.");
            SelectedAccount = existing;
            Changed(nameof(HasAccounts)); Changed(nameof(HasNoAccounts));
            await SaveAsync();
        }
        finally
        {
            if (reconnecting is not null) reconnecting.IsRefreshing = false;
            RefreshCommand.Refresh();
            _loginGate.Release();
        }
    }

    public async Task RenameAsync(AccountViewModel account, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Escribí un nombre para la cuenta.");
        account.SetProfile(account.Profile with { DisplayName = name.Trim() });
        await SaveAsync();
    }

    public async Task RemoveAsync(AccountViewModel account)
    {
        if (account.IsRefreshing) throw new InvalidOperationException("Esperá a que termine el refresco de esta cuenta.");
        account.IsRefreshing = true;
        RefreshCommand.Refresh();
        try
        {
            await _usage.LogoutAsync(account.Id, _lifetime.Token);
            Accounts.Remove(account);
            if (SelectedAccount == account) SelectedAccount = Accounts.FirstOrDefault();
            Changed(nameof(HasAccounts)); Changed(nameof(HasNoAccounts));
            await SaveAsync();
        }
        finally { account.IsRefreshing = false; RefreshCommand.Refresh(); }
    }

    public async Task ConfigureAsync(string? executable, bool startWithWindows)
    {
        _settings = _settings with { CodexExecutablePath = string.IsNullOrWhiteSpace(executable) ? null : executable.Trim(), StartWithWindows = startWithWindows };
        await SaveAsync();
    }

    public async Task SavePositionAsync(double left, double top)
    {
        _settings = _settings with { Left = left, Top = top };
        await SaveAsync();
    }

    public async Task SaveAsync()
    {
        if (IsDemo || _lifetime.IsCancellationRequested) return;
        _settings = _settings with { Accounts = Accounts.Select(a => a.Profile).ToList(), SelectedAccountId = SelectedAccount?.Id };
        try { await _store.SaveAsync(_settings, _lifetime.Token); StorageError = null; }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { StorageError = "No se pudo guardar la configuración. Los cambios podrían perderse al cerrar."; }
    }

    public void Tick() { foreach (var account in Accounts) account.Tick(); }

    public static string SafeError(Exception exception) => exception switch
    {
        FileNotFoundException => "No se encontró Codex CLI. Configurá su ubicación para conectar y consultar las cuentas.",
        TimeoutException => "La consulta tardó demasiado. Se volverá a intentar en el próximo refresco.",
        InvalidDataException => exception.Message,
        InvalidOperationException => exception.Message,
        OperationCanceledException => "Se canceló la conexión o venció el tiempo de inicio de sesión.",
        _ => "No se pudo conectar. Comprobá Internet y volvé a conectar la cuenta desde Configuración."
    };

    public void Dispose() => _lifetime.Cancel();
}
