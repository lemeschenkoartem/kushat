using System.Text.Json.Serialization;
using Kushat.Core;

namespace Kushat.App;

public sealed class ConnectionProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public ReferenceConnection Connection { get; set; } = new();
    public LoginEnvelope? SavedLogin { get; set; }
    public bool RememberLogin { get; set; } = true;
    [JsonIgnore] public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? $"{Connection.ServerAddress} · {Connection.UserName}" : $"{Name} · {Connection.ServerAddress}";
}

public sealed class AppSettings
{
    // Retained for migration from 0.1.x and as the active connection used by the current session.
    public ReferenceConnection Connection { get; set; } = new();
    public List<ConnectionProfile> Profiles { get; set; } = [];
    public string? ActiveProfileId { get; set; }
    public string? PrinterName { get; set; }
    public string TemplateId { get; set; } = "classic";
    public LabelOrientation LabelOrientation { get; set; } = LabelOrientation.Portrait;
    public LabelFontSizes FontSizes { get; set; } = new();
    public string ThemeId { get; set; } = "mint";
    public double? LabelWidthMm { get; set; }
    public double? LabelHeightMm { get; set; }
    public List<LabelPreset> LabelPresets { get; set; } = [];
    public string? ActiveLabelPresetId { get; set; }
    public WeightPriceMode WeightPriceMode { get; set; } = WeightPriceMode.HundredGrams;
    [JsonIgnore] public ConnectionProfile? ActiveProfile => Profiles.FirstOrDefault(p => p.Id == ActiveProfileId);

    public void Normalize()
    {
        if (Connection is null || Profiles is null || Profiles.Any(p => p is null || p.Connection is null))
            throw new FormatException("Некорректные профили подключения.");
        if (Profiles.Count == 0 && !string.IsNullOrWhiteSpace(Connection.ServerAddress))
            Profiles.Add(new ConnectionProfile { Connection = Connection });
        var active = ActiveProfile ?? Profiles.FirstOrDefault();
        ActiveProfileId = active?.Id;
        if (active is not null) Connection = active.Connection;
        TemplateId = LabelTemplates.Find(TemplateId).Id;
        if (!Enum.IsDefined(LabelOrientation)) LabelOrientation = LabelOrientation.Portrait;
        FontSizes = LabelFontSizes.Normalize(FontSizes);
        ThemeId = ThemeId is "mint" or "indigo" or "graphite" ? ThemeId : "mint";
        var defaults = LabelLayout.Geometry(LabelOrientation);
        try { LabelLayout.Geometry(LabelWidthMm ?? defaults.LabelWidthMm, LabelHeightMm ?? defaults.LabelHeightMm); }
        catch (ArgumentException) { LabelWidthMm = defaults.LabelWidthMm; LabelHeightMm = defaults.LabelHeightMm; }
        LabelWidthMm ??= defaults.LabelWidthMm; LabelHeightMm ??= defaults.LabelHeightMm;
        LabelPresets ??= [];
        LabelPresets = LabelPresets.Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Name))
            .Where(p => { try { LabelLayout.Geometry(p.WidthMm, p.HeightMm); return true; } catch (ArgumentException) { return false; } })
            .DistinctBy(p => p.Id).ToList();
        foreach (var preset in LabelPresets)
        {
            preset.TemplateId = LabelTemplates.Find(preset.TemplateId).Id;
            preset.FontSizes = LabelFontSizes.Normalize(preset.FontSizes);
        }
        if (!LabelPresets.Any(p => p.Id == ActiveLabelPresetId)) ActiveLabelPresetId = null;
        WeightPriceMode = WeightPriceMode.HundredGrams; // Migrate old kg/both preferences to the current single-price templates.
    }

    public LabelPreset SaveLabelPreset(string name, string templateId, double widthMm, double heightMm, LabelFontSizes? fontSizes = null)
    {
        fontSizes ??= FontSizes;
        fontSizes.Validate();
        name = name.Trim();
        if (name.Length is < 1 or > 80) throw new ArgumentException("Укажите название шаблона: от 1 до 80 символов.");
        LabelLayout.Geometry(widthMm, heightMm);
        var preset = LabelPresets.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (preset is null) { preset = new LabelPreset(); LabelPresets.Add(preset); }
        preset.Name = name; preset.TemplateId = LabelTemplates.Find(templateId).Id; preset.WidthMm = widthMm; preset.HeightMm = heightMm; preset.FontSizes = fontSizes;
        ActiveLabelPresetId = preset.Id; TemplateId = preset.TemplateId; LabelWidthMm = widthMm; LabelHeightMm = heightMm; FontSizes = fontSizes;
        return preset;
    }

    public ConnectionProfile RememberConnection(ReferenceConnection connection, string name, LoginEnvelope? savedLogin)
    {
        var key = LoginEnvelope.Key(connection);
        var profile = Profiles.FirstOrDefault(p => SameAccount(p.Connection, key));
        if (profile is null) { profile = new ConnectionProfile(); Profiles.Add(profile); }
        profile.Connection = connection; profile.Name = name.Trim(); profile.SavedLogin = savedLogin;
        ActiveProfileId = profile.Id; Connection = connection;
        return profile;
    }

    private static bool SameAccount(ReferenceConnection connection, string key)
    {
        try { return LoginEnvelope.Key(connection) == key; }
        catch (ArgumentException) { return false; }
    }
}
