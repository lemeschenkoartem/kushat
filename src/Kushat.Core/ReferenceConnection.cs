namespace Kushat.Core;

// Passwords belong to a live session, never to persisted connection settings.
public sealed class ReferenceConnection
{
    public string ServerAddress { get; set; } = "";
    public string UserName { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 30;

    public Uri GetEndpoint()
    {
        var address = ServerAddress?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(address)) throw new ArgumentException("Укажите адрес реф-сервера и порт XML-интерфейса.");
        if (!address.Contains("://", StringComparison.Ordinal)) address = "http://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.AbsolutePath != "/" && uri.AbsolutePath != "/rk7api/v0/xmlinterface.xml"))
            throw new ArgumentException("Укажите HTTP- или HTTPS-адрес сервера с портом, например http://server:порт. Пароль вводится отдельно.");
        if (string.IsNullOrWhiteSpace(UserName) || UserName.Contains(':') || UserName.Any(char.IsControl))
            throw new ArgumentException("Укажите логин сотрудника без двоеточий и управляющих символов.");
        if (TimeoutSeconds is < 1 or > 300) throw new ArgumentException("Таймаут должен быть от 1 до 300 секунд.");
        return new UriBuilder(uri) { Path = "/rk7api/v0/xmlinterface.xml" }.Uri;
    }
}

public sealed record PriceType(string Id, string Name);
public sealed record ReferenceItem(string Id, string Name, IReadOnlyDictionary<string, decimal> Prices);
public sealed record PricedCatalog(IReadOnlyList<Product> Products, int WithoutPrice);

public sealed record ReferenceCatalog(IReadOnlyList<PriceType> PriceTypes, IReadOnlyList<ReferenceItem> Items)
{
    public PricedCatalog SelectPriceType(string id)
    {
        if (!PriceTypes.Any(p => p.Id == id)) throw new ArgumentException("Выберите тип цены из загруженного справочника.");
        var products = Items.Where(i => i.Prices.ContainsKey(id))
            .Select(i => new Product(i.Id, i.Name, i.Prices[id])).ToArray();
        if (products.Length == 0)
            throw new FormatException("Для выбранного типа цены нет значений. Проверьте цены и доступность макросвойств меню на сервере.");
        return new(products, Items.Count - products.Length);
    }
}
