using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Win32;
using OpenAIUsageWidget.Desktop;
using OpenAIUsageWidget.Presentation;
using OpenAIUsageWidget.Views;

namespace OpenAIUsageWidget;

public partial class App : Application
{
    private WidgetViewModel? _model;
    private MainWindow? _widget;
    private TrayIcon? _tray;
    private Mutex? _instance;
    private bool _ownsMutex;
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _clockTimer;
    private SettingsWindow? _settingsWindow;
    private ConnectWindow? _connectWindow;
    private bool _exiting;
    private BindingDiagnostics? _bindingDiagnostics;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var preview = ArgumentValue(e.Args, "--preview");
        var dropdownPreview = ArgumentValue(e.Args, "--preview-dropdown");
        var smoke = ArgumentValue(e.Args, "--smoke-test");
        var demo = e.Args.Contains("--demo") || preview is not null || dropdownPreview is not null || smoke is not null;
        var empty = e.Args.Contains("--empty");
        try
        {
            if (demo) _bindingDiagnostics = new BindingDiagnostics();
            if (!demo)
            {
                _instance = new Mutex(true, @"Local\OpenAIUsageWidget-" + Environment.UserName, out _ownsMutex);
                if (!_ownsMutex)
                {
                    MessageBox.Show("El widget ya está ejecutándose. Buscá su icono en el área de notificaciones para abrir la configuración.", "OpenAI Usage");
                    Shutdown(); return;
                }
            }
            var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAIUsageWidget");
            var store = new SettingsStore(dataDirectory);
            var settings = demo ? (empty ? new WidgetSettings() : DemoSettings()) : await store.LoadAsync();
            WidgetViewModel? model = null;
            var service = new CodexAccountService(store, () => model?.Settings.CodexExecutablePath);
            model = new WidgetViewModel(store, settings, service, demo);
            _model = model;
            _widget = new MainWindow(model, attachToDesktop: preview is null && dropdownPreview is null);
            MainWindow = _widget;
            model.SettingsRequested += OpenSettings;
            model.AddAccountRequested += OpenConnection;
            _widget.Closed += (_, _) => { if (!_exiting) Shutdown(); };
            _widget.Show();

            if (preview is not null || dropdownPreview is not null || smoke is not null)
            {
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (preview is not null) _widget.SavePreview(preview);
                if (dropdownPreview is not null) await _widget.SaveDropdownPreviewAsync(dropdownPreview);
                if (smoke is not null)
                {
                    // Construct all secondary views offline to catch XAML/binding regressions, without showing them.
                    new SettingsWindow(model).Close();
                    new ConnectWindow(model).Close();
                    _widget.Anchor?.Reposition();
                    var result = new
                    {
                        desktop = _widget.Anchor?.Diagnostics(),
                        accountCount = model.Accounts.Count,
                        selectedAccount = model.SelectedAccount?.DisplayName,
                        width = _widget.ActualWidth, height = _widget.ActualHeight,
                        readOnly = true, liveRequestsMade = false,
                        bindingErrors = _bindingDiagnostics?.Errors.ToArray(),
                        note = "No se enviaron teclas Win+D ni se alteraron las ventanas del usuario. Esa comprobación sigue siendo manual."
                    };
                    var path = Path.GetFullPath(smoke);
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllTextAsync(path, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                }
                Shutdown(); return;
            }

            _tray = new TrayIcon(OpenSettings, () => { _widget.ResetPosition(); _ = model.SavePositionAsync(_widget.Left, _widget.Top); },
                () => _ = model.RefreshAllAsync(), () => _ = ExitAsync());
            _refreshTimer = new DispatcherTimer(TimeSpan.FromMinutes(5), DispatcherPriority.Background,
                async (_, _) => await model.RefreshAllAsync(), Dispatcher);
            _clockTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background,
                (_, _) => model.Tick(), Dispatcher);
            if (!demo)
            {
                SystemEvents.PowerModeChanged += PowerModeChanged;
                await model.RefreshAllAsync();
            }
        }
        catch (Exception exception)
        {
            // Explicit offline diagnostic modes contain only fabricated data and never connect.
            if (preview is not null || dropdownPreview is not null || smoke is not null)
            {
                var errorPath = Path.ChangeExtension(Path.GetFullPath(preview ?? dropdownPreview ?? smoke!), ".error.txt");
                Directory.CreateDirectory(Path.GetDirectoryName(errorPath)!);
                await File.WriteAllTextAsync(errorPath, exception.ToString());
            }
            // Never print upstream data, tokens, browser URLs, or credential paths in a crash report.
            if (preview is null && dropdownPreview is null && smoke is null)
                MessageBox.Show("No se pudo iniciar el widget. Comprobá que la configuración local sea válida y accesible. No se reemplazaron tus archivos de configuración.",
                    "OpenAI Usage", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void OpenSettings()
    {
        if (_model is null || _model.IsDemo) return;
        if (_settingsWindow is not null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow(_model);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OpenConnection()
    {
        if (_model is null || _model.IsDemo) return;
        if (_connectWindow is not null) { _connectWindow.Activate(); return; }
        _connectWindow = new ConnectWindow(_model);
        try { _connectWindow.ShowDialog(); }
        finally { _connectWindow = null; }
    }

    private void PowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume && !Dispatcher.HasShutdownStarted)
            Dispatcher.BeginInvoke(new Action(async () => { if (_model is not null) await _model.RefreshAllAsync(onlyStale: true); }));
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        if (_model is not null && _widget is not null) await _model.SavePositionAsync(_widget.Left, _widget.Top);
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _refreshTimer?.Stop(); _clockTimer?.Stop();
        SystemEvents.PowerModeChanged -= PowerModeChanged;
        _model?.Dispose();
        _bindingDiagnostics?.Dispose();
        CodexRpcClient.StopOwnedProcesses();
        _tray?.Dispose();
        if (_ownsMutex) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private static string? ArgumentValue(string[] arguments, string name)
    {
        var index = Array.IndexOf(arguments, name);
        return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : null;
    }

    private static WidgetSettings DemoSettings()
    {
        var now = DateTimeOffset.UtcNow;
        var personal = new AccountProfile
        {
            Id = Guid.Parse("0068f600-b75c-4574-9750-0ee038903899"), DisplayName = "Cuenta personal", Plan = "plus",
            LastUsage = new UsageSnapshot(null, "plus", new UsageWindow(12, 300, now.AddHours(3).AddMinutes(47)),
                new UsageWindow(26, 10080, now.AddDays(5).AddHours(9)), 3, now)
        };
        var work = new AccountProfile { Id = Guid.Parse("014a3601-a6a6-4b39-b399-7c4fb05dd7cc"), DisplayName = "Cuenta de trabajo", Plan = "free" };
        return new WidgetSettings { Accounts = [personal, work], SelectedAccountId = personal.Id };
    }
}
