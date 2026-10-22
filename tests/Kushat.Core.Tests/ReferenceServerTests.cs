using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Kushat.Core;
using Xunit;

namespace Kushat.Core.Tests;

public class ReferenceServerTests
{
    private static ReferenceConnection Connection() => new() { ServerAddress = "https://reference.test:7443", UserName = "employee" };
    private static string Response(string items) => "<RK7QueryResult Status=\"Ok\"><CommandResult CMD=\"GetRefData\" Status=\"Ok\"><RK7Reference><Items>" + items + "</Items></RK7Reference></CommandResult></RK7QueryResult>";
    private static readonly string Types = Response("<Item Ident=\"3\" Name=\"Основная\"/><Item Ident=\"5\" Name=\"Доставка\"/>");
    private static readonly string Menu = Response("<Item Ident=\"100\" Name=\"Оливье\" PriceMode=\"pmPerUnitOfWeight\" PortionName=\"кг\" PRICETYPES-3=\"85055.00\" PRICETYPES-5=\"90000.00\"/><Item Ident=\"200\" Name=\"Плов\" PriceMode=\"pmPerPiece\" PRICETYPES-3=\"65000.00\"/>");

    [Theory]
    [InlineData("reference.test:8080", "http://reference.test:8080/rk7api/v0/xmlinterface.xml")]
    [InlineData("127.0.0.1:8080", "http://127.0.0.1:8080/rk7api/v0/xmlinterface.xml")]
    [InlineData("http://reference.test:8080/", "http://reference.test:8080/rk7api/v0/xmlinterface.xml")]
    [InlineData("http://reference.test:8080/rk7api/v0/xmlinterface.xml", "http://reference.test:8080/rk7api/v0/xmlinterface.xml")]
    [InlineData("https://reference.test:7443/", "https://reference.test:7443/rk7api/v0/xmlinterface.xml")]
    [InlineData("https://reference.test:7443/rk7api/v0/xmlinterface.xml", "https://reference.test:7443/rk7api/v0/xmlinterface.xml")]
    public void ResolvesServerAndPortWithoutSavingPassword(string address, string expected)
    {
        var c = Connection(); c.ServerAddress = address;
        Assert.Equal(expected, c.GetEndpoint().ToString());
        Assert.DoesNotContain("Password", JsonSerializer.Serialize(c));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://reference.test:7443")]
    [InlineData("https://user:password@reference.test/")]
    [InlineData("https://reference.test/?password=test")]
    [InlineData("https://reference.test/unknown")]
    public void RejectsInvalidAddresses(string address)
    {
        var c = Connection(); c.ServerAddress = address;
        Assert.Throws<ArgumentException>(() => c.GetEndpoint());
    }

    [Theory]
    [InlineData("http")]
    [InlineData("https")]
    public async Task AuthenticatesEmployeeAndReadsProductsPricesAndMenuGroups(string scheme)
    {
        var calls = new List<string>();
        using var http = new HttpClient(new StubHandler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/rk7api/v0/xmlinterface.xml", request.RequestUri!.AbsolutePath);
            Assert.Equal(scheme, request.RequestUri.Scheme);
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("employee:test-password")), request.Headers.Authorization?.Parameter);
            var body = XDocument.Parse(await request.Content!.ReadAsStringAsync());
            var command = body.Root!.Element("RK7Command")!;
            Assert.Equal("GetRefData", (string?)command.Attribute("CMD"));
            Assert.Equal("1", (string?)command.Attribute("OnlyActive"));
            Assert.Equal("1", (string?)command.Attribute("WithMacroProp"));
            var reference = (string)command.Attribute("RefName")!;
            calls.Add(reference);
            Assert.Equal(reference == "CategList" ? "1" : "2", (string?)command.Attribute("WithChildItems"));
            return Xml(reference == "PriceTypes" ? Types : reference == "CategList" ? Response("<Item Ident=\"10\" Name=\"Кухня\" Parent=\"0\"/>") : Menu);
        }));
        var connection = Connection();
        connection.ServerAddress = scheme + "://reference.test:7443";
        var catalog = await new ReferenceServerClient(http).ConnectAsync(connection, "test-password");
        Assert.Equal(new[] { "PriceTypes", "MenuItems", "CategList" }, calls);
        Assert.Equal(2, catalog.Items.Count);
        Assert.Equal(2, catalog.PriceTypes.Count);
        Assert.Single(catalog.MenuGroups);
        var priced = catalog.SelectPriceType("3");
        Assert.Equal(850.55m, priced.Products[0].PricePerKg);
        Assert.Equal(85.06m, priced.Products[0].PricePer100Grams);
    }

    [Fact]
    public void UsesExplicitPriceTypeAndReportsItemsWithoutThatPrice()
    {
        var catalog = new ReferenceCatalog(ReferenceServerClient.ParsePriceTypes(XDocument.Parse(Types)),
            ReferenceServerClient.ParseMenu(XDocument.Parse(Menu)));
        var priced = catalog.SelectPriceType("5");
        Assert.Single(priced.Products); Assert.Equal(900m, priced.Products[0].PricePerKg);
        Assert.Equal(1, priced.WithoutPrice);
        Assert.Throws<ArgumentException>(() => catalog.SelectPriceType("unknown"));
    }

    [Theory]
    [InlineData("<Item Ident=\"1\" Name=\"Товар\"/>")]
    [InlineData("<Item Ident=\"1\" Name=\"Товар\" PRICETYPES-3=\"-1\"/>")]
    [InlineData("<Item Ident=\"1\" Name=\"Товар\" PRICETYPES-3=\"85,5\"/>")]
    [InlineData("<Item Ident=\"1\" Name=\"\" PRICETYPES-3=\"100\"/>")]
    [InlineData("<Item Ident=\"1\" Name=\"А\" PRICETYPES-3=\"100\"/><Item Ident=\"1\" Name=\"Б\" PRICETYPES-3=\"200\"/>")]
    public void RefusesInvalidPricesAndDuplicateIdentities(string items) =>
        Assert.Throws<FormatException>(() => ReferenceServerClient.ParseMenu(XDocument.Parse(Response(items))));

    [Fact]
    public void IgnoresInactiveItemsAndEmptyPricesButPreservesZeroPrice()
    {
        var items = ReferenceServerClient.ParseMenu(XDocument.Parse(Response(
            "<Item Ident=\"1\" Name=\"Активный\" Status=\"rsActive\" PRICETYPES-3=\"0\" PRICETYPES-5=\"\"/>" +
            "<Item Ident=\"2\" Name=\"Удалённый\" Status=\"rsDeleted\" PRICETYPES-3=\"200\"/>")));
        Assert.Single(items); Assert.Equal(0m, items[0].Prices["3"]);
        Assert.False(items[0].Prices.ContainsKey("5"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)] [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Redirect)] [InlineData(HttpStatusCode.InternalServerError)]
    public async Task RejectsHttpErrorsWithoutLeakingResponse(HttpStatusCode status)
    {
        using var http = new HttpClient(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(status)
            { Content = new StringContent("private-response") })));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => new ReferenceServerClient(http).ConnectAsync(Connection(), "test-password"));
        Assert.Equal(status, error.StatusCode); Assert.DoesNotContain("private-response", error.Message);
        Assert.DoesNotContain("test-password", error.Message);
    }

    [Theory]
    [InlineData("<html>login</html>")]
    [InlineData("<RK7QueryResult Status=\"Error\" ErrorText=\"private-text\"/>")]
    [InlineData("<RK7QueryResult Status=\"Ok\"><CommandResult CMD=\"GetRefData\" Status=\"Error\" ErrorText=\"private-text\"/></RK7QueryResult>")]
    [InlineData("<!DOCTYPE RK7QueryResult [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><RK7QueryResult>&e;</RK7QueryResult>")]
    public async Task RefusesInvalidXmlErrorStatusesAndDtd(string xml)
    {
        using var http = new HttpClient(new StubHandler(_ => Task.FromResult(Xml(xml))));
        var error = await Assert.ThrowsAsync<FormatException>(() => new ReferenceServerClient(http).ConnectAsync(Connection(), "test-password"));
        Assert.DoesNotContain("private-text", error.Message);
    }

    [Fact]
    public async Task MenuFailureDoesNotEstablishAnAuthenticatedCatalog()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHandler(_ => Task.FromResult(Xml(++calls == 1 ? Types : "<html/>"))));
        await Assert.ThrowsAsync<FormatException>(() => new ReferenceServerClient(http).ConnectAsync(Connection(), "test-password"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task RejectsMissingPasswordBeforeNetworkAccess()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("No network expected")));
        await Assert.ThrowsAsync<ArgumentException>(() => new ReferenceServerClient(http).ConnectAsync(Connection(), ""));
    }

    private static HttpResponseMessage Xml(string xml) => new(HttpStatusCode.OK)
        { Content = new StringContent(xml, Encoding.UTF8, "application/xml") };
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request);
    }
}
