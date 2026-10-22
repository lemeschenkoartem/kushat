using System.Text.Json;
using Kushat.App;
using Kushat.Core;
using Xunit;

namespace Kushat.Core.Tests;

public class LabelOrientationTests
{
    [Theory]
    [InlineData(LabelOrientation.Portrait, 210, 297, 100, 140)]
    [InlineData(LabelOrientation.Landscape, 297, 210, 140, 100)]
    public void FourPhysicalLabelsFitChosenA4OrientationWithoutScaling(LabelOrientation orientation, double pageWidth, double pageHeight, double width, double height)
    {
        var geometry = LabelLayout.Geometry(orientation);
        Assert.Equal(pageWidth, geometry.PageWidthMm); Assert.Equal(pageHeight, geometry.PageHeightMm);
        Assert.Equal(width, geometry.LabelWidthMm); Assert.Equal(height, geometry.LabelHeightMm);
        var page = Assert.Single(LabelLayout.Create([new(new("1", "Товар", 123m), 4)]));
        foreach (var slot in page.Slots)
        {
            Assert.InRange(geometry.LeftMm + (slot.Column + 1) * width, 0, pageWidth);
            Assert.InRange(geometry.TopMm + (slot.Row + 1) * height, 0, pageHeight);
        }
        Assert.True(LabelLayout.FitsImageableArea(geometry.LeftMm, geometry.TopMm, 2 * width, 2 * height, orientation));
        Assert.False(LabelLayout.FitsImageableArea(geometry.LeftMm + 1, geometry.TopMm, 2 * width - 1, 2 * height, orientation));
    }

    [Fact]
    public void OrientationSurvivesSettingsRoundTripAndOldSettingsRemainPortrait()
    {
        var settings = new AppSettings { LabelOrientation = LabelOrientation.Landscape };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!; restored.Normalize();
        Assert.Equal(LabelOrientation.Landscape, restored.LabelOrientation);
        var old = JsonSerializer.Deserialize<AppSettings>("{}")!; old.Normalize(); Assert.Equal(LabelOrientation.Portrait, old.LabelOrientation);
        old.LabelOrientation = (LabelOrientation)999; old.Normalize(); Assert.Equal(LabelOrientation.Portrait, old.LabelOrientation);
    }
}
