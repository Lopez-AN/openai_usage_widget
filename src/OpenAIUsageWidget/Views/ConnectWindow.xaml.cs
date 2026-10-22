using System.Diagnostics;
using System.ComponentModel;
using OpenAIUsageWidget.Presentation;

namespace OpenAIUsageWidget.Views;

public partial class ConnectWindow : Window
{
    private readonly WidgetViewModel _model;
    private readonly Guid? _reconnectId;
    private readonly CancellationTokenSource _cancellation = new();
    private bool _busy;

    public ConnectWindow(WidgetViewModel model, AccountViewModel? reconnect = null)
    {
        _model = model;
        _reconnectId = reconnect?.Id;
        InitializeComponent();
        if (reconnect is not null) AccountName.Text = reconnect.DisplayName;
        CheckConnector();
        Closing += OnClosing;
    }

    private void CheckConnector()
    {
        var available = CodexExecutable.Find(_model.Settings.CodexExecutablePath) is not null;
        ConnectorHint.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
        ConnectButton.IsEnabled = available && !_busy;
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var name = AccountName.Text.Trim();
        if (string.IsNullOrWhiteSpace(name)) { StatusText.Text = "Escribí un nombre para esta cuenta."; return; }
        _busy = true;
        ConnectButton.IsEnabled = false;
        AccountName.IsEnabled = false;
        BusyIndicator.Visibility = Visibility.Visible;
        StatusText.Text = "Preparando el inicio de sesión…";
        try
        {
            await _model.ConnectAsync(name, uri =>
            {
                StatusText.Text = "Completá el inicio de sesión en el navegador. Para otra cuenta, elegí la cuenta correcta allí.";
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }, _cancellation.Token, _reconnectId);
            if (!_cancellation.IsCancellationRequested) DialogResult = true;
        }
        catch (Exception exception)
        {
            if (!_cancellation.IsCancellationRequested) StatusText.Text = WidgetViewModel.SafeError(exception);
        }
        finally
        {
            _busy = false;
            AccountName.IsEnabled = true;
            BusyIndicator.Visibility = Visibility.Collapsed;
            CheckConnector();
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e) => _cancellation.Cancel();
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
    private void Download_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/openai/codex#installing-and-running-codex-cli") { UseShellExecute = true }); }
        catch { StatusText.Text = "No se pudo abrir el navegador. La descarga oficial está en github.com/openai/codex."; }
    }
    private void Configure_Click(object sender, RoutedEventArgs e)
    {
        new SettingsWindow(_model) { Owner = this }.ShowDialog();
        CheckConnector();
    }
}
