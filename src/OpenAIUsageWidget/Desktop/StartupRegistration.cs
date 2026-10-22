using Microsoft.Win32;

namespace OpenAIUsageWidget.Desktop;

public static class StartupRegistration
{
    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No se pudo ubicar el ejecutable.");
            var command = $"\"{executable}\"";
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                var assembly = System.Reflection.Assembly.GetEntryAssembly()?.Location;
                if (string.IsNullOrWhiteSpace(assembly)) throw new InvalidOperationException("Ejecutá la app desde su archivo .exe para activar el inicio automático.");
                command += $" \"{assembly}\"";
            }
            key.SetValue("OpenAIUsageWidget", command);
        }
        else key.DeleteValue("OpenAIUsageWidget", throwOnMissingValue: false);
    }
}
