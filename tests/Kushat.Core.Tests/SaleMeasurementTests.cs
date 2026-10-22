using System.Xml.Linq;
using Kushat.Core;
using Xunit;

namespace Kushat.Core.Tests;

public class SaleMeasurementTests
{
    private static IReadOnlyList<ReferenceItem> Parse(string attributes) => ReferenceServerClient.ParseMenu(XDocument.Parse(
        "<RK7QueryResult Status=\"Ok\"><RK7Reference><Items><Item Ident=\"1\" Name=\"Товар\" " + attributes +
        "/></Items></RK7Reference></RK7QueryResult>"));

    [Theory]
    [InlineData("pmPerPiece", "", 14500, 145, 145)]
    [InlineData("pmPerPiece", "", 5555, 55.55, 55.55)]
    [InlineData("pmPerUnitOfWeight", "кг", 35000, 350, 35)]
    public void NativeKopecksMatchRKeeperPiecePriceOrConvertKilogramToHundredGrams(string mode,string unit,decimal kopecks,decimal rubles,decimal printed)
    {
        var item=Parse($"PriceMode=\"{mode}\" PortionName=\"{unit}\" PRICETYPES-3=\"{kopecks}\"").Single();
        var product=new Product(item.Id,item.Name,item.Prices["3"],item.Unit);
        Assert.Equal(rubles,product.Price);
        Assert.Equal(printed,LabelPrices.For(product,WeightPriceMode.HundredGrams).Primary.Amount);
    }

    [Fact]
    public void StandardPortionPriceBecomesKilogramAndHundredGramPrices()
    {
        var items = Parse("PriceMode=\"pmPerPortion\" PortionName=\"кг\" PortionWeight=\"250\" PRICETYPES-3=\"35000\"");
        var product = new ReferenceCatalog([new("3", "Основной")], items).SelectPriceType("3").Products.Single();
        Assert.Equal(SaleUnit.Kilogram, product.Unit);
        Assert.Equal(1400m, product.Price);
        Assert.Equal(140m, product.PricePer100Grams);
    }

    [Fact]
    public void PortionConversionDividesOnceToAvoidRoundingHalfACentDown()
    {
        var item = Parse("PriceMode=\"pmPerPortion\" PortionName=\"кг\" PortionWeight=\"1200\" PRICETYPES-3=\"303.00\"").Single();
        var product = new Product(item.Id, item.Name, item.Prices["3"], item.Unit);
        Assert.Equal(2.53m, product.DisplayPrice);
    }

    [Fact]
    public void PieceGoodsNeverAcquireHundredGramPriceEvenWithFractionalQuantityPrecision()
    {
        var items = Parse("PriceMode=\"pmPerPiece\" PortionWeight=\"250\" QntDecDigits=\"3\" PRICETYPES-3=\"5500\"");
        var product = new ReferenceCatalog([new("3", "Основной")], items).SelectPriceType("3").Products.Single();
        Assert.Equal(SaleUnit.Piece, product.Unit);
        Assert.Equal(55m, product.Price);
        Assert.Null(product.PricePer100Grams);
        Assert.Equal("Цена за 1 шт.", product.PriceTitle);
    }

    [Theory]
    [InlineData("", "кг", "250")]
    [InlineData("unknown", "кг", "250")]
    [InlineData("pmPerUnitOfWeight", "л", "250")]
    [InlineData("pmPerUnitOfWeight", "", "250")]
    [InlineData("pmPerPortion", "кг", "0")]
    [InlineData("pmPerPortion", "кг", "-100")]
    [InlineData("pmPerPortion", "кг", "0.25")]
    public void AmbiguousModesUnitsAndInvalidPortionsRemainVisibleButCannotPrint(string mode, string unit, string grams)
    {
        var items = Parse($"PriceMode=\"{mode}\" PortionName=\"{unit}\" PortionWeight=\"{grams}\" PRICETYPES-3=\"5500\"");
        var product = new ReferenceCatalog([new("3", "Основной")], items).SelectPriceType("3").Products.Single();
        Assert.False(product.CanPrint);
        Assert.NotEmpty(product.UnitError!);
        Assert.Throws<ArgumentException>(() => LabelLayout.Create([new(product, 1)]));
    }

    [Fact]
    public void WeightPerKilogramNeedsNoPortionConversion()
    {
        var item = Parse("PriceMode=\"pmPerUnitOfWeight\" PortionName=\"кг\" PortionWeight=\"250\" PRICETYPES-3=\"140000\"").Single();
        Assert.Equal(SaleUnit.Kilogram, item.Unit);
        Assert.Equal(1400m, item.Prices["3"]);
    }

    [Fact]
    public void UnsetPriceSentinelIsNotPrintedAsAHugePrice()
    {
        var item = Parse("PriceMode=\"pmPerPiece\" PRICETYPES-3=\"9223372036854775807\" PRICETYPES-5=\"5500\"").Single();
        Assert.False(item.Prices.ContainsKey("3"));
        Assert.Equal(55m, item.Prices["5"]);
    }

    [Theory]
    [InlineData("Основной")]
    [InlineData(" основная ")]
    [InlineData("ОСНОВНОЙ")]
    public void PrimaryPriceTypeIsChosenByNameEvenWhenItIsNotFirst(string name)
    {
        var catalog = new ReferenceCatalog([new("5", "Доставка"), new("3", name)], []);
        Assert.Equal("3", catalog.DefaultPriceTypeId);
    }

    [Fact]
    public void AmbiguousPriceTypesAreNotSilentlyReplacedByFirstOne()
    {
        var catalog = new ReferenceCatalog([new("5", "Доставка"), new("4", "Банкет")], []);
        Assert.Null(catalog.DefaultPriceTypeId);
    }
}
