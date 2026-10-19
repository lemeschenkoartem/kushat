using Kushat.Core;
using Xunit;

namespace Kushat.Core.Tests;

public class LabelTests
{
    [Theory]
    [InlineData("850", "85")]
    [InlineData("315.55", "31.56")]
    [InlineData("315.54", "31.55")]
    [InlineData("0", "0")]
    [InlineData("0.05", "0.01")]
    public void CalculatesHundredGramsWithDecimalRounding(string kg, string expected)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        Assert.Equal(decimal.Parse(expected, culture), Money.Per100Grams(decimal.Parse(kg, culture)));
    }

    [Fact]
    public void MultipleProductsKeepTheirQuantitiesAcrossPageBoundary()
    {
        var a = new Product("a", "Оливье", 850m);
        var b = new Product("b", "Плов", 650m);
        var pages = LabelLayout.Create([new(a, 3), new(b, 2)]);
        Assert.Equal(2, pages.Count);
        Assert.Equal(new[] { "a", "a", "a", "b", "b" }, pages.SelectMany(p => p.Slots).Select(s => s.Product.Id));
        Assert.Equal(new[] { (0, 0), (0, 1), (1, 0), (1, 1) }, pages[0].Slots.Select(s => (s.Row, s.Column)));
        Assert.Single(pages[1].Slots);
        Assert.Equal((0, 0), (pages[1].Slots[0].Row, pages[1].Slots[0].Column));
    }

    [Theory]
    [InlineData(1, 1)] [InlineData(4, 1)] [InlineData(5, 2)] [InlineData(8, 2)] [InlineData(999, 250)]
    public void PaginatesWithoutAnExtraBlankPage(int quantity, int pageCount)
    {
        var pages = LabelLayout.Create([new(new("a", "Товар", 1m), quantity)]);
        Assert.Equal(pageCount, pages.Count);
        Assert.Equal(quantity, pages.Sum(p => p.Slots.Count));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(1000)]
    public void RejectsInvalidQuantity(int quantity) => Assert.Throws<ArgumentException>(() =>
        LabelLayout.Create([new(new("a", "Товар", 1m), quantity)]));

    [Fact]
    public void RejectsEmptyOrderAndExcessiveTotal()
    {
        Assert.Throws<ArgumentException>(() => LabelLayout.Create([]));
        Assert.Throws<ArgumentException>(() => LabelLayout.Create([
            new(new("a", "Товар", 1m), 999), new(new("b", "Товар 2", 1m), 2)]));
    }

    [Theory]
    [InlineData(5, 8.5, 200, 280, true)]
    [InlineData(4, 4, 202, 289, true)]
    [InlineData(6, 4, 198, 289, false)]
    [InlineData(5, 10, 200, 277, false)]
    [InlineData(5, 8.5, 190, 280, false)]
    public void ChecksPrinterMarginsWithoutShrinking(double x, double y, double w, double h, bool fits) =>
        Assert.Equal(fits, LabelLayout.FitsImageableArea(x, y, w, h));
}
