using System.Text.Json;

namespace OpenAIUsageWidget.Core;

public sealed class SettingsStore(string directory)
{
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string FilePath => Path.Combine(DirectoryPath, "settings.json");
    public string ProfileDirectory(Guid id) => Path.Combine(DirectoryPath, "profiles", id.ToString("N"));

    public async Task<WidgetSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(FilePath)) return new WidgetSettings();
        try
        {
            await using var stream = File.OpenRead(FilePath);
            var settings = await JsonSerializer.DeserializeAsync<WidgetSettings>(stream, Options, cancellationToken)
                ?? throw new InvalidDataException("El archivo de configuración está vacío.");
            Validate(settings);
            return settings;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("No se pudo leer settings.json. El archivo no fue modificado.", exception);
        }
    }

    public async Task SaveAsync(WidgetSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        await _writeLock.WaitAsync(cancellationToken);
        var temporary = FilePath + ".tmp";
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
                await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            // This is only our own, precisely named atomic-write scratch file.
            if (File.Exists(temporary)) File.Delete(temporary);
            _writeLock.Release();
        }
    }

    private static void Validate(WidgetSettings settings)
    {
        if (settings.SchemaVersion != 1) throw new InvalidDataException("Esta versión de configuración no es compatible.");
        if (settings.Accounts is null || settings.Accounts.Any(a => a is null || a.Id == Guid.Empty || string.IsNullOrWhiteSpace(a.DisplayName)) ||
            settings.Accounts.Select(a => a.Id).Distinct().Count() != settings.Accounts.Count)
            throw new InvalidDataException("La lista de cuentas de settings.json no es válida.");
        if (settings.Left is { } x && !double.IsFinite(x) || settings.Top is { } y && !double.IsFinite(y))
            throw new InvalidDataException("La posición guardada no es válida.");
        foreach (var account in settings.Accounts)
        {
            var usage = account.LastUsage;
            if (usage is null) continue;
            if (usage.AvailableResets < 0 ||
                usage.FiveHour is { } five && !double.IsFinite(five.UsedPercent) ||
                usage.Weekly is { } week && !double.IsFinite(week.UsedPercent))
                throw new InvalidDataException("Los datos de uso guardados no son válidos.");
        }
    }
}
