namespace Kushat.Core;

public enum WeightPriceMode { Both, Kilogram, HundredGrams }
public sealed record PriceLine(string Title, decimal Amount);
public sealed record LabelPrices(PriceLine Primary, PriceLine? Secondary)
{
    public static LabelPrices For(Product product, WeightPriceMode mode, bool emphasizeHundredGrams = false)
    {
        if (!product.CanPrint) throw new ArgumentException("Не определена единица товара: " + product.Name);
        if (!Enum.IsDefined(mode)) throw new ArgumentException("Неизвестный режим вывода цены.");
        var basePrice = new PriceLine(product.PriceTitle, product.DisplayPrice);
        if (product.Unit == SaleUnit.Piece || mode == WeightPriceMode.Kilogram) return new(basePrice, null);
        var hundred = new PriceLine("Цена за 100 г", product.PricePer100Grams!.Value);
        if (mode == WeightPriceMode.HundredGrams) return new(hundred, null);
        return emphasizeHundredGrams ? new(hundred, basePrice) : new(basePrice, hundred);
    }
}
