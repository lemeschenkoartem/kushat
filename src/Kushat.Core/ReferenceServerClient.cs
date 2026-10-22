using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Kushat.Core;

/// <summary>Reads RK7 references over HTTP or HTTPS through its XML interface, without WSA2 or SQL.</summary>
public sealed class ReferenceServerClient(HttpClient http)
{
    private const int MaxResponseBytes = 16 * 1024 * 1024;

    public async Task<ReferenceCatalog> ConnectAsync(ReferenceConnection connection, string password,
        CancellationToken cancellationToken = default)
    {
        var endpoint = connection.GetEndpoint();
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Введите пароль сотрудника.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(connection.TimeoutSeconds));
        var types = await ReadReferenceAsync(endpoint, connection.UserName.Trim(), password, "PriceTypes", timeout.Token);
        var menu = await ReadReferenceAsync(endpoint, connection.UserName.Trim(), password, "MenuItems", timeout.Token);
        var priceTypes = ParsePriceTypes(types);
        var items = ParseMenu(menu);
        try
        {
            // CategList is the native menu-folder reference. Only its own hierarchy is needed.
            var groups = await ReadReferenceAsync(endpoint, connection.UserName.Trim(), password, "CategList", timeout.Token, 1);
            return new(priceTypes, items, ParseMenuGroups(groups));
        }
        catch (Exception ex) when (ex is FormatException or HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.NotFound })
        {
            return new(priceTypes, items, [], "Ветки меню не загружены. Показан общий список товаров. Проверьте доступ к группам меню на Reference-сервере.");
        }
    }

    private async Task<XDocument> ReadReferenceAsync(Uri endpoint, string login, string password, string reference, CancellationToken ct, int withChildItems = 2)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(login + ":" + password)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml"));
        var command = new XElement("RK7Command", new XAttribute("CMD", "GetRefData"),
            new XAttribute("RefName", reference), new XAttribute("OnlyActive", "1"),
            new XAttribute("WithChildItems", withChildItems.ToString(CultureInfo.InvariantCulture)), new XAttribute("WithMacroProp", "1"));
        request.Content = new StringContent(new XElement("RK7Query", command).ToString(SaveOptions.DisableFormatting),
            Encoding.UTF8, "application/xml");
        try { return await ReadResponseAsync(request, ct); }
        catch (HttpRequestException ex) when (ex.StatusCode is null)
        {
            request.Options.TryGetValue(ReferenceHttpClient.CertificateFailureKey, out var certificate);
            throw new ReferenceConnectionException(endpoint, ex, certificate);
        }
        catch (IOException ex)
        {
            request.Options.TryGetValue(ReferenceHttpClient.CertificateFailureKey, out var certificate);
            throw new ReferenceConnectionException(endpoint, ex, certificate);
        }
    }

    private async Task<XDocument> ReadResponseAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HttpRequestException("Сервер отклонил вход. Проверьте логин, пароль и права сотрудника на XML-интерфейс.", null, response.StatusCode);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Реф-сервер вернул HTTP {(int)response.StatusCode}. Проверьте адрес и порт XML-интерфейса.", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + count > MaxResponseBytes)
                throw new FormatException("Ответ реф-сервера превышает 16 МБ. Нужна поддержка ограниченной выборки.");
            buffer.Write(chunk, 0, count);
        }
        buffer.Position = 0;
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            using var reader = XmlReader.Create(buffer, new XmlReaderSettings
            { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxResponseBytes });
            var document = XDocument.Load(reader);
            ValidateResponse(document);
            return document;
        }
        catch (XmlException) { throw new FormatException("Сервер не вернул допустимый XML-ответ R-Keeper. Проверьте адрес и порт XML-интерфейса."); }
    }

    private static void ValidateResponse(XDocument document)
    {
        var root = document.Root;
        if (root?.Name != "RK7QueryResult") throw new FormatException("По этому адресу не получен ответ XML-интерфейса R-Keeper.");
        if ((string?)root.Attribute("Status") != "Ok")
            throw new FormatException("R-Keeper отклонил запрос. Проверьте права сотрудника, доступность и лицензию XML-интерфейса.");
        var commands = root.Elements("CommandResult").ToArray();
        if (commands.Length == 0 && root.Elements("RK7Reference").Count() == 1) return;
        if (commands.Length != 1 || (string?)commands[0].Attribute("Status") != "Ok" ||
            (string?)commands[0].Attribute("CMD") != "GetRefData")
            throw new FormatException("Не удалось прочитать справочник R-Keeper. Проверьте права сотрудника на чтение меню и типов цен.");
    }

    private static IEnumerable<XElement> Items(XDocument document)
    {
        ValidateResponse(document);
        var reference = (document.Root!.Element("CommandResult") ?? document.Root).Element("RK7Reference")
            ?? throw new FormatException("В ответе отсутствует справочник R-Keeper.");
        var items = reference.Element("Items") ?? throw new FormatException("В ответе отсутствует список элементов справочника.");
        // Only direct Items/Item nodes: descendants may include unrelated child objects.
        return items.Elements("Item").Where(i => i.Attribute("Status") is null || (string?)i.Attribute("Status") == "rsActive");
    }

    public static IReadOnlyList<PriceType> ParsePriceTypes(XDocument document)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var types = Items(document).Select(item =>
        {
            var id = Required(item, "Ident");
            if (!seen.Add(id)) throw new FormatException("В справочнике повторяются идентификаторы типов цен.");
            return new PriceType(id, Required(item, "Name"));
        }).ToArray();
        if (types.Length == 0) throw new FormatException("Реф-сервер не вернул доступные типы цен.");
        return types;
    }

    public static IReadOnlyList<ReferenceItem> ParseMenu(XDocument document)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ReferenceItem>();
        foreach (var item in Items(document))
        {
            var id = Required(item, "Ident");
            if (!seen.Add(id)) throw new FormatException("В меню повторяются идентификаторы товаров.");
            var measurement = SaleMeasurement.FromReference((string?)item.Attribute("PriceMode"),
                (string?)item.Attribute("PortionName"), (string?)item.Attribute("PortionWeight"));
            var prices = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var attribute in item.Attributes().Where(a => a.Name.LocalName.StartsWith("PRICETYPES-", StringComparison.Ordinal)))
            {
                var typeId = attribute.Name.LocalName[11..];
                // Empty macro values mean that this price type has no price for the dish.
                if (string.IsNullOrWhiteSpace(attribute.Value)) continue;
                if (attribute.Value == "9223372036854775807") continue;
                if (typeId.Length == 0 || !decimal.TryParse(attribute.Value,
                    NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var price) || price < 0)
                    throw new FormatException("В меню обнаружена некорректная цена. Загрузка остановлена.");
                // Native PRICETYPES money is in kopecks. Convert once to rubles, then apply portion/weight conversion without intermediate rounding.
                try { prices.Add(typeId, (price / 100m) / measurement.PriceDivisor); }
                catch (OverflowException) { throw new FormatException("Цена после пересчёта выходит за допустимый диапазон."); }
            }
            result.Add(new ReferenceItem(id, Required(item, "Name"), prices, measurement.Unit, measurement.Error, ParentId(item)));
        }
        if (result.Count == 0) throw new FormatException("Реф-сервер вернул пустое меню.");
        if (!result.Any(i => i.Prices.Count > 0))
            throw new FormatException("Реф-сервер не вернул цены PRICETYPES-… в меню. Нужно проверить макросвойства XML-интерфейса или адаптировать чтение цен по ответу сервера.");
        return result;
    }

    public static IReadOnlyList<MenuGroup> ParseMenuGroups(XDocument document)
    {
        ValidateResponse(document);
        var reference = (document.Root!.Element("CommandResult") ?? document.Root).Element("RK7Reference");
        if (reference?.Attribute("Name") is { } name && !name.Value.Equals("CategList", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("В ответе получен другой справочник вместо групп меню.");
        var groups = new Dictionary<string, MenuGroup>(StringComparer.Ordinal);
        var pending = new Stack<(XElement Element, string? Parent)>();
        foreach (var item in Items(document)) pending.Push((item, null));
        while (pending.TryPop(out var entry))
        {
            var item = entry.Element;
            if (item.Attribute("Status") is { Value: not "rsActive" }) continue;
            var id = ((string?)item.Attribute("Ident") ?? (string?)item.Attribute("ItemIdent"))?.Trim();
            if (string.IsNullOrEmpty(id)) throw new FormatException("У группы меню отсутствует идентификатор.");
            var parent = item.Attribute("Parent") is not null || item.Attribute("MainParentIdent") is not null ? ParentId(item) : entry.Parent;
            var group = new MenuGroup(id, Required(item, "Name"), parent);
            if (groups.TryGetValue(id, out var previous) && previous != group)
                throw new FormatException("Группа меню повторяется с разными свойствами.");
            groups[id] = group;
            // WithChildItems=1 nests groups here. Never interpret dishes, modifiers or arbitrary descendants as folders.
            foreach (var child in item.Element("RIChildItems")?.Elements("TCategListItem") ?? [])
                pending.Push((child, id));
        }
        return groups.Values.ToArray();
    }

    private static string? ParentId(XElement item)
    {
        var parent = (string?)item.Attribute("Parent");
        if (string.IsNullOrWhiteSpace(parent)) parent = (string?)item.Attribute("MainParentIdent");
        parent = parent?.Trim();
        return string.IsNullOrEmpty(parent) || parent == "0" ? null : parent;
    }

    private static string Required(XElement item, string name)
    {
        var value = ((string?)item.Attribute(name))?.Trim();
        return !string.IsNullOrWhiteSpace(value) ? value : throw new FormatException("В справочнике отсутствует идентификатор или наименование.");
    }
}
