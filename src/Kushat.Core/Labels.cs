namespace Kushat.Core;

public sealed record Product(string Id, string Name, decimal Price, SaleUnit Unit = SaleUnit.Kilogram, string? UnitError = null, string? MenuGroupId = null)
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

public enum LabelOrientation { Portrait, Landscape }

public sealed record LabelGeometry(double PageWidthMm, double PageHeightMm, double LabelWidthMm, double LabelHeightMm, double LeftMm, double TopMm, int Columns = 2, int Rows = 2)
{
    public int Capacity => Columns * Rows;
}

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

    public static IReadOnlyList<LabelPage> Create(IEnumerable<LabelOrder> orders, LabelGeometry? geometry = null)
    {
        geometry ??= Geometry();
        if (geometry.Capacity < 1 || geometry.Columns < 1 || geometry.Rows < 1) throw new ArgumentException("Неверная раскладка ценников.");
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
        return labels.Chunk(geometry.Capacity).Select(chunk => new LabelPage(chunk.Select((p, i) =>
            new LabelSlot(p, i / geometry.Columns, i % geometry.Columns)).ToArray())).ToArray();
    }

    public static LabelGeometry Geometry(LabelOrientation orientation = LabelOrientation.Portrait) => orientation switch
    {
        LabelOrientation.Portrait => new(210, 297, 100, 140, 5, 8.5),
        LabelOrientation.Landscape => new(297, 210, 140, 100, 8.5, 5),
        _ => throw new ArgumentException("Неизвестная ориентация ценника.")
    };

    public static LabelGeometry Geometry(double widthMm, double heightMm)
    {
        if (!double.IsFinite(widthMm) || !double.IsFinite(heightMm) || widthMm < 20 || heightMm < 20 || widthMm > 287 || heightMm > 287)
            throw new ArgumentException("Ширина и высота: от 20 до 287 мм. Ценник должен помещаться на А4 с полями 5 мм.");
        LabelGeometry Candidate(double pageWidth, double pageHeight)
        {
            var columns = (int)Math.Floor((pageWidth - 10 + 0.000001) / widthMm);
            var rows = (int)Math.Floor((pageHeight - 10 + 0.000001) / heightMm);
            return new(pageWidth, pageHeight, widthMm, heightMm, (pageWidth - columns * widthMm) / 2,
                (pageHeight - rows * heightMm) / 2, columns, rows);
        }
        var portrait = Candidate(210, 297); var landscape = Candidate(297, 210);
        var best = landscape.Capacity > portrait.Capacity || landscape.Capacity == portrait.Capacity && widthMm > heightMm ? landscape : portrait;
        if (best.Capacity == 0) throw new ArgumentException("Ценник такого размера не помещается на А4. Уменьшите ширину или высоту.");
        return best;
    }

    public static bool FitsImageableArea(double x, double y, double width, double height, LabelOrientation orientation = LabelOrientation.Portrait) =>
        FitsImageableArea(x, y, width, height, Geometry(orientation));

    public static bool FitsImageableArea(double x, double y, double width, double height, LabelGeometry geometry)
    {
        const double tolerance = 0.2;
        return x <= geometry.LeftMm + tolerance && y <= geometry.TopMm + tolerance &&
            x + width >= geometry.LeftMm + geometry.Columns * geometry.LabelWidthMm - tolerance &&
            y + height >= geometry.TopMm + geometry.Rows * geometry.LabelHeightMm - tolerance;
    }
}
