using System.Globalization;

namespace Kushat.Core;

// Points are physical typographic points (1/72 inch), independent of label size.
// A null value retains the template's automatic sizing from earlier versions.
public sealed record LabelFontSizes(double? NamePt = null, double? PricePt = null)
{
    public static bool IsValid(double? points) => points is null || double.IsFinite(points.Value) && points >= 1 && points <= 1638;

    public void Validate()
    {
        if (!IsValid(NamePt) || !IsValid(PricePt))
            throw new ArgumentException("Размер шрифта: от 1 до 1638 пт, как в Word, или «Авто».");
    }

    public static LabelFontSizes Normalize(LabelFontSizes? sizes) => new(
        IsValid(sizes?.NamePt) ? sizes?.NamePt : null,
        IsValid(sizes?.PricePt) ? sizes?.PricePt : null);

    public static double? Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Equals("Авто", StringComparison.OrdinalIgnoreCase)) return null;
        if (!double.TryParse(text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) || !IsValid(value))
            throw new ArgumentException("Размер шрифта: от 1 до 1638 пт, как в Word, или «Авто».");
        return value;
    }
}
