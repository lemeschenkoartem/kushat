using System.Text.Json;
using Kushat.App;
using Kushat.Core;
using Xunit;
namespace Kushat.Core.Tests;
public class CustomLabelTests
{
    [Theory]
    [InlineData(100,140,4)] [InlineData(140,100,4)] [InlineData(80,50,12)] [InlineData(50,80,12)] [InlineData(60,40,21)]
    public void CustomSizesPaginateWithoutOverlapOrLeavingA4(double width, double height, int capacity)
    {
        var geometry = LabelLayout.Geometry(width,height);
        Assert.Equal(capacity,geometry.Capacity);
        var pages = LabelLayout.Create([new(new("1","Товар",145,SaleUnit.Piece),capacity+1)],geometry);
        Assert.Equal(2,pages.Count); Assert.Equal(capacity,pages[0].Slots.Count); Assert.Single(pages[1].Slots);
        foreach(var slot in pages.SelectMany(p=>p.Slots))
        {
            Assert.InRange(geometry.LeftMm+slot.Column*width,5,geometry.PageWidthMm-width-5+0.001);
            Assert.InRange(geometry.TopMm+slot.Row*height,5,geometry.PageHeightMm-height-5+0.001);
        }
        Assert.True(LabelLayout.FitsImageableArea(5,5,geometry.PageWidthMm-10,geometry.PageHeightMm-10,geometry));
    }
    [Theory]
    [InlineData(0,100)] [InlineData(250,250)] [InlineData(300,50)] [InlineData(double.NaN,50)] [InlineData(50,double.PositiveInfinity)]
    public void RejectsInvalidDimensions(double width,double height) => Assert.Throws<ArgumentException>(()=>LabelLayout.Geometry(width,height));
    [Fact]
    public void NamedPresetsAndThemeSurviveRestartAndUpdateWithoutDuplicates()
    {
        var settings=new AppSettings { ThemeId="graphite" }; settings.Normalize();
        var first=settings.SaveLabelPreset("Мой ценник","chalkboard",80,50);
        var id=first.Id; settings.SaveLabelPreset("мой ценник","cafe",90,60);
        var restored=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!; restored.Normalize();
        var preset=Assert.Single(restored.LabelPresets);
        Assert.Equal(id,preset.Id); Assert.Equal(id,restored.ActiveLabelPresetId);
        Assert.Equal("cafe",restored.TemplateId); Assert.Equal(90d,restored.LabelWidthMm); Assert.Equal(60d,restored.LabelHeightMm); Assert.Equal("graphite",restored.ThemeId);
    }
    [Fact]
    public void ExistingHorizontalPreferenceMigratesWithoutLosingOrientation()
    {
        var settings=new AppSettings {LabelOrientation=LabelOrientation.Landscape}; settings.Normalize();
        Assert.Equal(140d,settings.LabelWidthMm); Assert.Equal(100d,settings.LabelHeightMm);
    }
}
