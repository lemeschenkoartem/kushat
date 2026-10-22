using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Kushat.Core;

namespace Kushat.App;

public sealed class ProductRow(Product product) : INotifyPropertyChanged, IDataErrorInfo
{
    public Product Product { get; } = product;
    public string Name => Product.Name;
    public string Id => Product.Id;
    public string Price => (Product.Unit == SaleUnit.Kilogram ? Product.PricePer100Grams!.Value : Product.DisplayPrice).ToString("N2", CultureInfo.GetCultureInfo("ru-RU"));
    public string PricePer100Grams => Product.PricePer100Grams?.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) ?? "—";
    public string UnitTitle => Product.Unit == SaleUnit.Kilogram ? "100 г" : Product.UnitTitle;
    public string? UnitError => Product.UnitError;
    public bool CanPrint => Product.CanPrint;
    private bool selected;
    public bool IsSelected { get => selected; set { selected = value && CanPrint; Changed(); } }
    private string quantity = "1";
    public string Quantity { get => quantity; set { quantity = value; Changed(); } }
    public bool TryQuantity(out int result) => int.TryParse(Quantity, NumberStyles.None,
        CultureInfo.InvariantCulture, out result) && result is >= 1 and <= LabelLayout.MaxQuantity;
    public string Error => "";
    public string this[string columnName] => columnName == nameof(Quantity) && !TryQuantity(out _)
        ? $"Введите целое число от 1 до {LabelLayout.MaxQuantity}." : "";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
