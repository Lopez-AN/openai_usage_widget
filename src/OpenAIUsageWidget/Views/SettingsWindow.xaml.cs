using System.Diagnostics;
using System.Windows.Controls;
using Microsoft.Win32;
using OpenAIUsageWidget.Desktop;
using OpenAIUsageWidget.Presentation;

namespace OpenAIUsageWidget.Views;

public partial class SettingsWindow : Window
{
    private readonly WidgetViewModel _model;
    private bool _busy;
    public SettingsWindow(WidgetViewModel model)
    {
        _model = model;
        InitializeComponent();
        DataContext = model;
        ExecutableText.Text = model.Settings.CodexExecutablePath ?? "";
        AutostartCheck.IsChecked = model.Settings.StartWithWindows;
        AccountList.SelectedItem = model.SelectedAccount;
    }

    private void AccountList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AliasText is not null) AliasText.Text = (AccountList.SelectedItem as AccountViewModel)?.DisplayName ?? "";
    }

    private async Task ExecuteAsync(Func<Task> operation)
    {
        if (_busy) return;
        _busy = true;
        IsEnabled = false;
        StatusText.Text = "";
        try { await operation(); }
        catch (Exception exception) { StatusText.Text = WidgetViewModel.SafeError(exception); }
        finally { IsEnabled = true; _busy = false; }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedItem is AccountViewModel account)
            await ExecuteAsync(() => _model.RenameAsync(account, AliasText.Text));
    }

    private void Add_Click(object sender, RoutedEventArgs e) => new ConnectWindow(_model) { Owner = this }.ShowDialog();
    private void Reconnect_Click(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedItem is AccountViewModel account)
            new ConnectWindow(_model, account) { Owner = this }.ShowDialog();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (AccountList.SelectedItem is not AccountViewModel account) return;
        if (MessageBox.Show(this, "Se quitará esta cuenta y se cerrará la sesión propia del widget.\n\nNo se modifica tu cuenta de OpenAI ni las sesiones de otras aplicaciones.",
            "Quitar cuenta", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await ExecuteAsync(() => _model.RemoveAsync(account));
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "Seleccionar Codex CLI", Filter = "Codex CLI|codex.exe;codex.cmd|Ejecutables|*.exe;*.cmd", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) ExecutableText.Text = dialog.FileName;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var path = ExecutableText.Text.Trim();
        if (path.Length > 0 && CodexExecutable.Find(path) is null)
        { StatusText.Text = "La ruta debe apuntar a un codex.exe o codex.cmd existente."; return; }
        await ExecuteAsync(async () =>
        {
            var enabled = AutostartCheck.IsChecked == true;
            if (enabled != _model.Settings.StartWithWindows) StartupRegistration.SetEnabled(enabled);
            await _model.ConfigureAsync(path, enabled);
            StatusText.Text = _model.StorageError ?? "Configuración guardada.";
        });
    }

    private void OpenData_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_model.StorageDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { _model.StorageDirectory }, UseShellExecute = true });
        }
        catch { StatusText.Text = "No se pudo abrir la carpeta de datos locales."; }
    }
}
