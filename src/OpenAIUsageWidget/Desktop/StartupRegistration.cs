using Microsoft.Win32;

namespace OpenAIUsageWidget.Desktop;

public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "OpenAIUsageWidget";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return IsEnabled(key);
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        SetEnabled(key, enabled, Environment.ProcessPath, System.Reflection.Assembly.GetEntryAssembly()?.Location);
    }

    // Accept an isolated key for tests; never change the real user's startup there.
    internal static bool IsEnabled(RegistryKey? key) =>
        key?.GetValue(ValueName) is string command && !string.IsNullOrWhiteSpace(command);

    internal static void SetEnabled(RegistryKey key, bool enabled, string? executable, string? assembly = null)
    {
        if (enabled)
        {
            if (string.IsNullOrWhiteSpace(executable)) throw new InvalidOperationException("No se pudo ubicar el ejecutable.");
            var command = $"\"{executable}\"";
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(assembly)) throw new InvalidOperationException("Ejecutá la app desde su archivo .exe para activar el inicio automático.");
                command += $" \"{assembly}\"";
            }
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
