namespace Kushat.Core;

public sealed record Product(string Id, string Name, decimal Price, SaleUnit Unit = SaleUnit.Kilogram, string? UnitError = null)
{
    public decimal DisplayPrice => decimal.Round(Price, 2, MidpointRounding.AwayFromZero);
    public decimal PricePerKg => Unit == SaleUnit.Kilogram ? DisplayPrice : throw new InvalidOperationException("Товар не весовой.");
    public decimal? PricePer100Grams => Unit == SaleUnit.Kilogram ? Money.Per100Grams(Price) : null;
    public bool CanPrint => Unit is SaleUnit.Kilogram or SaleUnit.Piece;
    public string UnitTitle => Unit switch { SaleUnit.Kilogram => "кг", SaleUnit.Piece => "шт.", _ => "не определено" };
    public string PriceTitle => Unit == SaleUnit.Piece ? "Цена за 1 шт." : "Цена за 1 кг";
}

public static class Money
{
    public static decimal Per100Grams(decimal pricePerKg) =>
        decimal.Round(pricePerKg / 10m, 2, MidpointRounding.AwayFromZero);
}

public sealed record LabelOrder(Product Product, int Quantity);
public sealed record LabelSlot(Product Product, int Row, int Column);
public sealed record LabelPage(IReadOnlyList<LabelSlot> Slots);

public static class LabelLayout
{
    public const int MaxQuantity = 999;
    public const int MaxLabels = 1000;
    public const double PageWidthMm = 210;
    public const double PageHeightMm = 297;
    public const double LabelWidthMm = 100;
    public const double LabelHeightMm = 140;
    public const double LeftMm = 5;
    public const double TopMm = 8.5;

    public static IReadOnlyList<LabelPage> Create(IEnumerable<LabelOrder> orders)
    {
        var labels = new List<Product>();
        foreach (var order in orders)
        {
            if (order.Quantity is < 1 or > MaxQuantity)
                throw new ArgumentException($"Количество ценников: от 1 до {MaxQuantity}.");
            if (string.IsNullOrWhiteSpace(order.Product.Name) || order.Product.Price < 0)
                throw new ArgumentException("У товара должны быть наименование и неотрицательная цена.");
            if (!order.Product.CanPrint) throw new ArgumentException("Не определена единица товара: " + order.Product.Name);
            if (labels.Count + order.Quantity > MaxLabels)
                throw new ArgumentException($"За один раз можно печатать до {MaxLabels} ценников.");
            labels.AddRange(Enumerable.Repeat(order.Product, order.Quantity));
        }
        if (labels.Count == 0) throw new ArgumentException("Выберите хотя бы один товар.");
        return labels.Chunk(4).Select(chunk => new LabelPage(chunk.Select((p, i) =>
            new LabelSlot(p, i / 2, i % 2)).ToArray())).ToArray();
    }

    public static bool FitsImageableArea(double x, double y, double width, double height)
    {
        const double tolerance = 0.2;
        return x <= LeftMm + tolerance && y <= TopMm + tolerance &&
            x + width >= LeftMm + 2 * LabelWidthMm - tolerance &&
            y + height >= TopMm + 2 * LabelHeightMm - tolerance;
    }
}
