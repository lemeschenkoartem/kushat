using System.Net;
using System.Text;
using System.Xml.Linq;
using Kushat.Core;
using Xunit;

namespace Kushat.Core.Tests;

public class MenuBrowserTests
{
    private static string Response(string items, string name = "CATEGLIST") =>
        $"<RK7QueryResult Status=\"Ok\"><RK7Reference Name=\"{name}\"><Items>{items}</Items></RK7Reference></RK7QueryResult>";
    private static MenuGroup[] Groups => [new("kitchen", "Кухня"), new("salads", "Салаты", "kitchen"), new("hot", "Горячее", "kitchen"), new("bakery", "Выпечка")];
    private static Product[] Products => [new("1", "Салат Оливье", 850m, MenuGroupId: "salads"), new("2", "Плов", 650m, MenuGroupId: "hot"), new("3", "Пирожок", 55m, SaleUnit.Piece, MenuGroupId: "bakery"), new("4", "Товар без папки", 10m), new("5", "Товар из отсутствующей группы", 11m, MenuGroupId: "missing")];

    [Fact]
    public void ParsesFlatAndNestedNativeFoldersWithoutIncludingDishOrModifierChildren()
    {
        var xml = Response("""
            <Item Ident="kitchen" Name="Кухня" Parent="0"><RIChildItems>
              <TCategListItem ItemIdent="salads" Name="Салаты" MainParentIdent="kitchen"/>
              <TCategListItem Ident="hot" Name="Горячее"><RIChildItems>
                <TCategListItem Ident="soups" Name="Супы"/>
                <TRK7MenuItem Ident="dish" Name="Борщ"/>
              </RIChildItems></TCategListItem>
              <TCategListItem Ident="deleted" Name="Удалённая" Status="rsDeleted"/>
              <TModifier Ident="modifier" Name="Добавка"/>
            </RIChildItems></Item>
            <Item Ident="bakery" Name="Выпечка" MainParentIdent="0"/>
            <Item Ident="salads" Name="Салаты" Parent="kitchen"/>
            """);
        var groups = ReferenceServerClient.ParseMenuGroups(XDocument.Parse(xml));
        Assert.Equal(5, groups.Count);
        Assert.Equal("kitchen", groups.Single(g => g.Id == "salads").ParentId);
        Assert.Equal("kitchen", groups.Single(g => g.Id == "hot").ParentId);
        Assert.Equal("hot", groups.Single(g => g.Id == "soups").ParentId);
        Assert.DoesNotContain(groups, g => g.Id is "dish" or "modifier" or "deleted");
    }

    [Fact]
    public void ParentLinksSurvivePriceSelectionAndStandardPortionConversion()
    {
        var xml = Response("<Item Ident=\"1\" Name=\"Оливье\" Parent=\"salads\" PriceMode=\"pmPerPortion\" PortionName=\"кг\" PortionWeight=\"250\" PRICETYPES-3=\"35000\"/>", "MENUITEMS");
        var catalog = new ReferenceCatalog([new("3", "Основной")], ReferenceServerClient.ParseMenu(XDocument.Parse(xml)), Groups);
        var product = Assert.Single(catalog.SelectPriceType("3").Products);
        Assert.Equal("salads", product.MenuGroupId); Assert.Equal(140m, product.PricePer100Grams);
    }

    [Fact]
    public void SelectingParentIncludesDescendantsAndSearchRemainsInsideBranch()
    {
        var browser = new MenuBrowser(Groups, Products);
        Assert.Equal(5, browser.Root.ProductCount);
        browser.Select(browser.FindGroup("kitchen")!);
        Assert.Equal(2, browser.SelectedBranch.ProductCount);
        Assert.Equal(new[] { "1", "2" }, Products.Where(p => browser.Matches(p, "")).Select(p => p.Id));
        Assert.Single(Products, p => browser.Matches(p, " оЛИВЬЕ "));
        Assert.DoesNotContain(Products, p => browser.Matches(p, "Пирожок"));
        browser.Select(browser.FindGroup("salads")!);
        Assert.Equal("Кухня / Салаты", browser.SelectedBranch.Path);
        Assert.Single(Products, p => browser.Matches(p, ""));
        browser.Select(browser.Root); Assert.Equal(5, Products.Count(p => browser.Matches(p, "")));
    }

    [Fact]
    public void ProductsWithoutAccessibleGroupAreAlwaysReachable()
    {
        var browser = new MenuBrowser(Groups, Products);
        var without = browser.Root.Children.Single(b => b.Kind == MenuBranchKind.Unassigned);
        browser.Select(without);
        Assert.Equal(new[] { "4", "5" }, Products.Where(p => browser.Matches(p, null)).Select(p => p.Id));
        var noGroups = new MenuBrowser([], Products); Assert.Equal(5, noGroups.Root.ProductCount);
        Assert.Equal(5, noGroups.Root.Children.Single().ProductCount);
    }

    [Fact]
    public void MissingParentsSelfLinksAndCyclesDoNotHideProductsOrLoop()
    {
        MenuGroup[] groups = [new("a", "Одинаковое имя", "b"), new("b", "Одинаковое имя", "a"), new("c", "Сирота", "absent"), new("d", "Самоссылка", "d")];
        var products = groups.Select(g => new Product(g.Id, g.Name, 10m, MenuGroupId: g.Id)).ToArray();
        var browser = new MenuBrowser(groups, products);
        Assert.True(browser.HasIncompleteHierarchy); Assert.Equal(4, browser.Root.ProductCount);
        Assert.Equal(2, browser.FindGroup("a")!.ProductCount);
        Assert.NotEqual(browser.FindGroup("a"), browser.FindGroup("b"));
        browser.Select(browser.FindGroup("a")!); Assert.Equal(2, products.Count(p => browser.Matches(p, "")));
        Assert.Contains(browser.Root.Children, b => b.GroupId == "c"); Assert.Contains(browser.Root.Children, b => b.GroupId == "d");
    }

    [Fact]
    public void DeepMenuIsBuiltAndFilteredWithoutRecursiveTraversal()
    {
        var groups = Enumerable.Range(1, 2000).Select(n => new MenuGroup(n.ToString(), "Группа " + n, n == 1 ? null : (n - 1).ToString()));
        Product[] products = [new("item", "Товар", 10m, MenuGroupId: "2000")];
        var browser = new MenuBrowser(groups, products); browser.Select(browser.FindGroup("1")!);
        Assert.True(browser.Matches(products[0], "Товар")); Assert.Equal(1, browser.Root.ProductCount);
        Assert.EndsWith("Группа 2000", browser.FindGroup("2000")!.Path);
    }

    [Theory]
    [InlineData("<Item Ident=\"1\" Name=\"А\"/><Item Ident=\"1\" Name=\"Б\"/>")]
    [InlineData("<Item Ident=\"1\"/>")]
    [InlineData("<Item Name=\"А\"/>")]
    public void InvalidGroupDefinitionsAreNotSilentlyAccepted(string items) =>
        Assert.Throws<FormatException>(() => ReferenceServerClient.ParseMenuGroups(XDocument.Parse(Response(items))));

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task UnavailableFolderReferenceDoesNotDiscardSuccessfullyLoadedProducts(HttpStatusCode status)
    {
        using var http = new HttpClient(new Handler(async request =>
        {
            var reference = (string?)XDocument.Parse(await request.Content!.ReadAsStringAsync()).Root!.Element("RK7Command")!.Attribute("RefName");
            if (reference == "CategList") return new(status) { Content = new StringContent("private-response") };
            return new(HttpStatusCode.OK) { Content = new StringContent(reference == "PriceTypes"
                ? Response("<Item Ident=\"3\" Name=\"Основной\"/>", "PRICETYPES")
                : Response("<Item Ident=\"1\" Name=\"Товар\" PriceMode=\"pmPerPiece\" Parent=\"g\" PRICETYPES-3=\"5500\"/>", "MENUITEMS"), Encoding.UTF8, "application/xml") };
        }));
        var catalog = await new ReferenceServerClient(http).ConnectAsync(new() { ServerAddress = "http://reference.test:8010", UserName = "labels" }, "password");
        Assert.Single(catalog.SelectPriceType("3").Products); Assert.Empty(catalog.MenuGroups);
        Assert.NotNull(catalog.GroupWarning); Assert.DoesNotContain("private-response", catalog.GroupWarning);
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request);
    }
}
