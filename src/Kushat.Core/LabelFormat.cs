namespace Kushat.Core;

public sealed class LabelPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string TemplateId { get; set; } = "classic";
    public double WidthMm { get; set; } = 100;
    public double HeightMm { get; set; } = 140;
    public LabelFontSizes FontSizes { get; set; } = new();
    public string DisplayName => $"{Name} · {WidthMm:0.##} × {HeightMm:0.##} мм";
}
