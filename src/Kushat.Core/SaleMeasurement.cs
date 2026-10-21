using System.Globalization;

namespace Kushat.Core;

public enum SaleUnit { Unknown, Kilogram, Piece }

public sealed record SaleMeasurement(SaleUnit Unit, decimal PriceDivisor, string? Error = null)
{
    public static SaleMeasurement FromReference(string? priceMode, string? portionName, string? portionWeight)
    {
        // PriceMode is the actual RK7 sale mode. Quantity precision and dish names do not identify weight goods.
        if (priceMode == "pmPerPiece") return new(SaleUnit.Piece, 1m);
        if (priceMode is not ("pmPerUnitOfWeight" or "pmPerPortion"))
            return new(SaleUnit.Unknown, 1m, string.IsNullOrWhiteSpace(priceMode)
                ? "R-Keeper не передал режим цены." : "Неподдерживаемый режим цены: " + priceMode);
        var unit = portionName?.Trim().TrimEnd('.').ToLowerInvariant();
        if (unit is not ("кг" or "kg" or "килограмм" or "килограммы"))
            return new(SaleUnit.Unknown, 1m, "Для весового товара нужна подтверждённая единица «кг». В R-Keeper: " +
                (string.IsNullOrWhiteSpace(portionName) ? "не указана" : portionName));
        if (priceMode == "pmPerUnitOfWeight") return new(SaleUnit.Kilogram, 1m);
        // Native XML PortionWeight is an integer quantity in grams (1000 = 1 kg).
        if (!long.TryParse(portionWeight, NumberStyles.None, CultureInfo.InvariantCulture, out var grams) || grams <= 0)
            return new(SaleUnit.Unknown, 1m, "Не получен корректный вес стандартной порции в граммах.");
        return new(SaleUnit.Kilogram, grams / 1000m);
    }
}
