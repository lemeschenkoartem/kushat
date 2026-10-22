using System.Windows;
using System.Windows.Media;
namespace Kushat.App;
public sealed record UiTheme(string Id, string Name);
public static class ThemeManager
{
    public static IReadOnlyList<UiTheme> All { get; } = [new("mint", "Мята · светлая"), new("indigo", "Индиго · светлая"), new("graphite", "Графит · тёмная")];
    public static void Apply(Window window, string id)
    {
        var colors = id switch
        {
            "graphite" => new[] { "#152238", "#26324B", "#1E2C42", "#283950", "#EDF5FF", "#B3C4D9", "#087D88", "#214D57", "#40536B" },
            "indigo" => new[] { "#EFEBFF", "#EAF5FF", "#FFFFFF", "#F2F0FC", "#252344", "#686383", "#6655CF", "#E9E3FF", "#DCD7ED" },
            _ => new[] { "#EAF7F2", "#F2F5FF", "#FFFFFF", "#F0F6F4", "#182D2A", "#566D68", "#087F68", "#D9F2E9", "#CBDFD8" }
        };
        var resources = new Dictionary<string, Brush> { ["AppBackgroundBrush"] = new LinearGradientBrush(ColorOf(colors[0]), ColorOf(colors[1]), 45) };
        string[] names = ["SurfaceBrush", "SurfaceAltBrush", "TextBrush", "MutedBrush", "AccentBrush", "AccentSoftBrush", "BorderBrush"];
        for (var i = 0; i < names.Length; i++) resources[names[i]] = new SolidColorBrush(ColorOf(colors[i + 2]));
        foreach (var (key, brush) in resources) { window.Resources[key] = brush; if (Application.Current is { } app) app.Resources[key] = brush; }
    }
    private static Color ColorOf(string value) => (Color)ColorConverter.ConvertFromString(value);
}
