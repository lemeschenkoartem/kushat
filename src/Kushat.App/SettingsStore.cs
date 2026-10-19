using System.IO;
using System.Text.Json;
using Kushat.Core;

namespace Kushat.App;

public sealed class AppSettings
{
    public ReferenceConnection Connection { get; set; } = new();
    public string? PrinterName { get; set; }
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public static string FilePath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.LocalApplicationData), "Kushat", "settings.json");

    public static AppSettings Load()
    {
        if (!File.Exists(FilePath)) return new();
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions)
            ?? throw new FormatException("Файл настроек пуст.");
        if (settings.Connection is null) throw new FormatException("Отсутствует профиль подключения.");
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, FilePath, overwrite: true);
    }
}
