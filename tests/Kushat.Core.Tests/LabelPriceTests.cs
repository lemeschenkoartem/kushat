using System.Text.Json;
using Kushat.App;
using Kushat.Core;
using Xunit;

namespace Kushat.Core.Tests;

public class LabelPriceTests
{
    [Theory]
    [InlineData(WeightPriceMode.Kilogram, "Цена за 1 кг", "315.55")]
    [InlineData(WeightPriceMode.HundredGrams, "Цена за 100 г", "31.56")]
    public void OnlyTheChosenWeightPriceIsPrinted(WeightPriceMode mode, string title, string amount)
    {
        foreach (var emphasize in new[] { true, false })
        {
            var lines = LabelPrices.For(new Product("1", "Товар", 315.55m), mode, emphasize);
            Assert.Equal(title, lines.Primary.Title);
            Assert.Equal(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), lines.Primary.Amount);
            Assert.Null(lines.Secondary);
        }
    }

    [Theory]
    [InlineData(WeightPriceMode.Both)]
    [InlineData(WeightPriceMode.Kilogram)]
    [InlineData(WeightPriceMode.HundredGrams)]
    public void PieceGoodsAlwaysShowOnlyPiecePrice(WeightPriceMode mode)
    {
        var lines = LabelPrices.For(new Product("1", "Пирожок", 55m, SaleUnit.Piece), mode, true);
        Assert.Equal("Цена за 1 шт.", lines.Primary.Title);
        Assert.Equal(55m, lines.Primary.Amount);
        Assert.Null(lines.Secondary);
    }

    [Fact]
    public void BothPricesCanEmphasizeHundredGramsWithoutLosingKilogramPrice()
    {
        var product = new Product("1", "Салат", 315.55m);
        var regular = LabelPrices.For(product, WeightPriceMode.Both);
        var large = LabelPrices.For(product, WeightPriceMode.Both, true);
        Assert.Equal(regular.Primary, large.Secondary);
        Assert.Equal(regular.Secondary, large.Primary);
    }

    [Fact]
    public void HundredGramPriceDoesNotRoundAnAlreadyRoundedKilogramPrice()
    {
        var portion = SaleMeasurement.FromReference("pmPerPortion", "кг", "151");
        var product = new Product("1", "Товар", 1.91m / portion.PriceDivisor, portion.Unit);
        Assert.Equal(12.65m, product.DisplayPrice);
        Assert.Equal(1.26m, LabelPrices.For(product, WeightPriceMode.HundredGrams).Primary.Amount);
    }

    [Fact]
    public void WeightPriceChoiceSurvivesSettingsRoundTrip()
    {
        var settings = new AppSettings { WeightPriceMode = WeightPriceMode.HundredGrams };
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        restored.Normalize();
        Assert.Equal(WeightPriceMode.HundredGrams, restored.WeightPriceMode);
    }
}
