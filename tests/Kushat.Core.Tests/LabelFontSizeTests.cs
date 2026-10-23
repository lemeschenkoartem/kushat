using System.Text.Json;
using Kushat.App;
using Kushat.Core;
using Xunit;

namespace Kushat.Core.Tests;

public class LabelFontSizeTests
{
    [Theory]
    [InlineData("18", 18)] [InlineData("18,5", 18.5)] [InlineData(" 18.5 ", 18.5)]
    [InlineData("1", 1)] [InlineData("1638", 1638)]
    public void ParsesWordPointSizesAndBothDecimalSeparators(string text, double expected) => Assert.Equal(expected, LabelFontSizes.Parse(text));

    [Theory]
    [InlineData("")] [InlineData("Авто")] [InlineData(" авто ")]
    public void AutoRetainsTemplateSizing(string text) => Assert.Null(LabelFontSizes.Parse(text));

    [Theory]
    [InlineData("0")] [InlineData("-12")] [InlineData("1639")] [InlineData("NaN")]
    [InlineData("Infinity")] [InlineData("12 пт")] [InlineData("18,5.5")]
    public void InvalidInputCannotBecomeAPrintSize(string text) => Assert.Throws<ArgumentException>(() => LabelFontSizes.Parse(text));

    [Fact]
    public void BuiltInAndSavedTemplateDisplayNeverExposesProgrammingNames()
    {
        foreach(var template in LabelTemplates.All)
        {
            Assert.Equal(template.Name,template.ToString());
            Assert.Matches(@"\p{IsCyrillic}",template.ToString());
            Assert.DoesNotMatch("[A-Za-z]",template.ToString());
            Assert.DoesNotMatch("[A-Za-z]",template.Description);
        }
        var preset=new LabelPreset {Name="Мой ценник",TemplateId="chalkboard",WidthMm=80,HeightMm=50};
        Assert.Equal(preset.DisplayName,preset.ToString());
        Assert.Equal("Мой ценник · 80 × 50 мм",preset.ToString());
    }

    [Fact]
    public void PointSizesSurviveSettingsAndPresetSerializationWithoutOverwritingOtherPresets()
    {
        var settings = new AppSettings(); settings.Normalize();
        var first = settings.SaveLabelPreset("Мелкий", "classic", 80, 50, new(10.5, 18.5));
        var second = settings.SaveLabelPreset("Крупный", "chalkboard", 100, 140, new(24, 36));
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!; restored.Normalize();
        Assert.Equal(new LabelFontSizes(24, 36), restored.FontSizes);
        Assert.Equal(new LabelFontSizes(10.5, 18.5), restored.LabelPresets.Single(p => p.Id == first.Id).FontSizes);
        Assert.Equal(new LabelFontSizes(24, 36), restored.LabelPresets.Single(p => p.Id == second.Id).FontSizes);
        restored.SaveLabelPreset("крупный", "chalkboard", 100, 140, new(null, 28));
        Assert.Equal(2, restored.LabelPresets.Count);
        Assert.Equal(new LabelFontSizes(null, 28), restored.FontSizes);
    }

    [Fact]
    public void SettingsFromPreviousVersionKeepTheirAutomaticSizesAndExistingPresets()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"LabelPresets":[{"Id":"old","Name":"Старый","TemplateId":"cafe","WidthMm":80,"HeightMm":50}]}""")!;
        settings.Normalize();
        Assert.Equal(new LabelFontSizes(), settings.FontSizes);
        Assert.Equal(new LabelFontSizes(), settings.LabelPresets.Single().FontSizes);
    }

    [Fact]
    public void InvalidStoredFontsRecoverIndependentlyAndDoNotDeletePresets()
    {
        var settings = new AppSettings { FontSizes = new(-1, 24), LabelPresets = [new() { Name="Мой",FontSizes=new(18,2000) }] };
        settings.Normalize();
        Assert.Equal(new LabelFontSizes(null, 24), settings.FontSizes);
        Assert.Equal(new LabelFontSizes(18, null), settings.LabelPresets.Single().FontSizes);
    }
}
