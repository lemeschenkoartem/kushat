using System.Globalization;
using System.Net.Http;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kushat.App;
using Kushat.Core;
using Xunit;

namespace Kushat.Windows.Tests;

public class WindowsBehaviorTests
{
    [Fact]
    public void WindowIconCanBeLoadedFromItsApplicationResourceUri()
    {
        Sta(() =>
        {
            var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Kushat;component/Assets/Kushat.ico"));
            Assert.NotNull(resource);
            using var stream = resource.Stream;
            var frame = System.Windows.Media.Imaging.BitmapFrame.Create(stream);
            Assert.True(frame.PixelWidth >= 16);
        });
    }

    [Fact]
    public void CertificateWizardCannotContinueOrFinishBeforeInspection()
    {
        Sta(() =>
        {
            var wizard = new CertificateWizardWindow();
            var buttons = LogicalChildren(wizard).OfType<Button>().ToArray();
            Assert.False(buttons.Single(b => Equals(b.Content, "Далее →")).IsEnabled);
            Assert.Equal(Visibility.Collapsed, buttons.Single(b => Equals(b.Content, "Завершить")).Visibility);
            Assert.Equal(Visibility.Collapsed, buttons.Single(b => Equals(b.Content, "Остановить службу")).Visibility);
            Assert.False(buttons.Single(b => Equals(b.Content, "← Назад")).IsEnabled);
            Assert.True(buttons.Single(b => Equals(b.Content, "Отмена")).IsEnabled);
            Assert.All(LogicalChildren(wizard).OfType<TextBox>(), box => Assert.True(box.IsReadOnly));
            buttons.Single(b => Equals(b.Content, "Отмена")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
    }

    [Fact]
    public void WindowsDpapiRestoresSavedLoginUsingAnotherProtectorInstance()
    {
        var connection = new ReferenceConnection { ServerAddress = "https://ref.example:8010", UserName = "labels" };
        var saved = LoginEnvelope.Create(connection, "test DPAPI password", new WindowsLoginProtector());
        Assert.Equal("test DPAPI password", saved.Open(connection, new WindowsLoginProtector()));
        var bytes = Convert.FromBase64String(saved.Ciphertext);
        bytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => (saved with { Ciphertext = Convert.ToBase64String(bytes) })
            .Open(connection, new WindowsLoginProtector()));
    }

    [Fact]
    public void EditingAnAutofilledServerClearsPasswordBeforeItCanBeSent()
    {
        Sta(() =>
        {
            var settings = new AppSettings();
            var connection = new ReferenceConnection { ServerAddress = "https://ref.example:8010", UserName = "labels" };
            settings.RememberConnection(connection, "Test", LoginEnvelope.Create(connection, "test password", new WindowsLoginProtector()));
            using var http = new HttpClient();
            var login = new LoginWindow(settings, http);
            var password = LogicalChildren(login).OfType<PasswordBox>().Single();
            Assert.Equal("test password", password.Password);
            var address = LogicalChildren(login).OfType<TextBox>().Single(t => t.Text == connection.ServerAddress);
            address.Text = "https://other.example:9000";
            Assert.Equal("", password.Password);
            login.Close();
        });
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("large-100g")]
    [InlineData("accent")]
    [InlineData("chalkboard")]
    [InlineData("cafe")]
    public void EveryTemplateShowsSingleCorrectPriceAndDemoMarkWithinFixedDimensions(string id)
    {
        Sta(() =>
        {
            var product = new Product("1", "Салат из свежих овощей с зеленью и домашней заправкой", 315.55m);
            var label = LabelDocument.CreateLabel(product, true, LabelTemplates.Find(id));
            label.Measure(new Size(label.Width, label.Height));
            label.Arrange(new Rect(0, 0, label.Width, label.Height));
            label.UpdateLayout();
            Assert.Equal(LabelDocument.Mm(100), label.ActualWidth, 4);
            Assert.Equal(LabelDocument.Mm(140), label.ActualHeight, 4);
            var text = LogicalChildren(label).OfType<TextBlock>().ToArray();
            Assert.Contains(text, t => t.Text == product.Name);
            Assert.DoesNotContain(text, t => t.Text == "315,55 ₽");
            Assert.Contains(text, t => t.Text == "31,56 ₽");
            Assert.Contains(text, t => t.Text == "ДЕМОНСТРАЦИОННЫЕ ДАННЫЕ");
            foreach (var block in text)
            {
                var bounds = block.TransformToAncestor(label).TransformBounds(new Rect(new Point(), block.RenderSize));
                Assert.True(bounds.Bottom <= label.ActualHeight + 0.5, $"Text extends below the label in {id}: {block.Text}");
            }
            if (id == "chalkboard")
            {
                var background = Assert.IsType<SolidColorBrush>(label.Background);
                Assert.True(background.Color.R < 32);
                Assert.All(text, t => Assert.Equal(Colors.White, ((SolidColorBrush)t.Foreground).Color));
            }
        });
    }

    [Theory]
    [InlineData("classic")]
    [InlineData("large-100g")]
    [InlineData("accent")]
    [InlineData("chalkboard")]
    [InlineData("cafe")]
    public void HorizontalTemplatesKeepCompleteTextAboveSinglePriceInsidePhysicalLabel(string id)
    {
        Sta(() =>
        {
            var product = new Product("1", "Салат из свежих овощей с зеленью и домашней заправкой", 315.55m);
            var label = LabelDocument.CreateLabel(product, true, LabelTemplates.Find(id), LabelOrientation.Landscape);
            label.Measure(new Size(label.Width, label.Height)); label.Arrange(new Rect(0, 0, label.Width, label.Height)); label.UpdateLayout();
            Assert.Equal(LabelDocument.Mm(140), label.ActualWidth, 4); Assert.Equal(LabelDocument.Mm(100), label.ActualHeight, 4);
            var text = LogicalChildren(label).OfType<TextBlock>().ToArray();
            Assert.Contains(text, t => t.Text == product.Name); Assert.DoesNotContain(text, t => t.Text == "315,55 ₽"); Assert.Contains(text, t => t.Text == "31,56 ₽");
            foreach (var block in text)
            {
                var bounds = block.TransformToAncestor(label).TransformBounds(new Rect(new Point(), block.RenderSize));
                Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5 && bounds.Right <= label.ActualWidth + 0.5 && bounds.Bottom <= label.ActualHeight + 0.5,
                    $"Text outside horizontal {id}: {block.Text}; {bounds}");
            }
            var document = LabelDocument.Create(LabelLayout.Create([new(product, 4)]), false, LabelTemplates.Find(id), LabelOrientation.Landscape);
            Assert.Equal(LabelDocument.Mm(297), document.DocumentPaginator.PageSize.Width, 4);
            Assert.Equal(LabelDocument.Mm(210), document.DocumentPaginator.PageSize.Height, 4);
            Assert.Equal(4, document.Pages[0].Child.Children.OfType<Border>().Count());
        });
    }

    public static IEnumerable<object[]> TemplateUnitsAndOrientations() =>
        from template in LabelTemplates.All
        from orientation in Enum.GetValues<LabelOrientation>()
        from unit in new[] { SaleUnit.Kilogram, SaleUnit.Piece }
        select new object[] { template.Id, orientation, unit };

    [Theory]
    [MemberData(nameof(TemplateUnitsAndOrientations))]
    public void EveryTemplatePlacesNameAboveOneHundredGramOrPiecePrice(string id, LabelOrientation orientation, SaleUnit unit)
    {
        Sta(() =>
        {
            var product = new Product("1", "Салат с зеленью и овощами", 55m, unit);
            var label = LabelDocument.CreateLabel(product, false, LabelTemplates.Find(id), orientation);
            label.Measure(new Size(label.Width, label.Height)); label.Arrange(new Rect(0, 0, label.Width, label.Height)); label.UpdateLayout();
            var text = LogicalChildren(label).OfType<TextBlock>().ToArray();
            Assert.DoesNotContain(text, t => t.Text == "Цена за 1 кг");
            Assert.Contains(text, t => t.Text == (unit == SaleUnit.Piece ? "Цена за 1 шт." : "Цена за 100 г"));
            Assert.DoesNotContain(text, t => t.Text == (unit == SaleUnit.Piece ? "Цена за 100 г" : "Цена за 1 шт."));
            var amount = Assert.Single(text, t => t.Text.EndsWith(" ₽"));
            Assert.Equal(unit == SaleUnit.Piece ? "55,00 ₽" : "5,50 ₽", amount.Text);
            var name = text.Single(t => t.Text == product.Name);
            var nameBounds = name.TransformToAncestor(label).TransformBounds(new Rect(new Point(), name.RenderSize));
            var priceBounds = amount.TransformToAncestor(label).TransformBounds(new Rect(new Point(), amount.RenderSize));
            Assert.True(nameBounds.Bottom < priceBounds.Top, $"Name is not above price: {id}, {orientation}");
            Assert.True(priceBounds.Top > label.ActualHeight / 2, $"Price is not in lower half: {id}, {orientation}");
        });
    }

    [Fact]
    public void MenuBranchAndSearchFilteringKeepSelectedRowsAndQuantitiesAcrossBranches()
    {
        Sta(() =>
        {
            var window = new MainWindow(new AppSettings(), false);
            try
            {
                window.AcceptCatalog(new ReferenceCatalog([new("3","Основной")], [
                    new("demo-1","Салат Оливье",new Dictionary<string,decimal>{{"3",850}},SaleUnit.Kilogram,MenuGroupId:"salads"),
                    new("demo-2","Плов",new Dictionary<string,decimal>{{"3",650}},SaleUnit.Kilogram,MenuGroupId:"hot"),
                    new("demo-3","Пирожок",new Dictionary<string,decimal>{{"3",55}},SaleUnit.Piece,MenuGroupId:"bakery"),
                    new("demo-4","Пюре",new Dictionary<string,decimal>{{"3",315.55m}},SaleUnit.Kilogram,MenuGroupId:"hot"),
                    new("demo-5","Салат овощной",new Dictionary<string,decimal>{{"3",420}},SaleUnit.Kilogram,MenuGroupId:"salads")
                ], [new("kitchen","Кухня"),new("salads","Салаты","kitchen"),new("hot","Горячие блюда","kitchen"),new("bakery","Выпечка")]),null);
                var grid = (DataGrid)window.FindName("ProductsGrid");
                var view = Assert.IsAssignableFrom<System.ComponentModel.ICollectionView>(grid.ItemsSource);
                var all = view.Cast<ProductRow>().ToArray(); Assert.Equal(5, all.Length);
                var selected = all.Single(r => r.Id == "demo-1"); Assert.Equal("85,00", selected.Price); Assert.Equal("100 г", selected.UnitTitle); selected.IsSelected = true; selected.Quantity = "3";
                var tree = (TreeView)window.FindName("MenuTree");
                var root = Assert.Single(tree.ItemsSource.Cast<MenuBranch>());
                var kitchen = root.Children.Single(b => b.GroupId == "kitchen");
                var salads = kitchen.Children.Single(b => b.GroupId == "salads");
                var hot = kitchen.Children.Single(b => b.GroupId == "hot");
                tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(root, salads, TreeView.SelectedItemChangedEvent));
                Assert.Equal(2, view.Cast<ProductRow>().Count());
                var search = (TextBox)window.FindName("SearchBox"); search.Text = "Оливье";
                Assert.Same(selected, Assert.Single(view.Cast<ProductRow>()));
                ((Button)window.FindName("SelectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                search.Text = "";
                tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(salads, hot, TreeView.SelectedItemChangedEvent));
                Assert.Equal(2, view.Cast<ProductRow>().Count()); Assert.True(selected.IsSelected); Assert.Equal("3", selected.Quantity);
                Assert.DoesNotContain(view.Cast<ProductRow>(), r => r.IsSelected);
                ((Button)window.FindName("SelectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(3, all.Count(r => r.IsSelected));
                Assert.Equal("3", selected.Quantity);
                tree.RaiseEvent(new RoutedPropertyChangedEventArgs<object>(hot, root, TreeView.SelectedItemChangedEvent));
                Assert.Equal(5, view.Cast<ProductRow>().Count());
                var summary = (TextBlock)window.FindName("SummaryText"); Assert.Contains("Ценников: 5", summary.Text);
                ((Button)window.FindName("ClearButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.DoesNotContain(all, r => r.IsSelected);
                Assert.Null(window.FindName("WeightPriceBox"));
            }
            finally { window.Close(); }
        });
    }

    [Theory]
    [InlineData("classic")] [InlineData("large-100g")] [InlineData("accent")] [InlineData("chalkboard")] [InlineData("cafe")]
    public void CustomPhysicalLabelKeepsAllTextAndExactPriceWithinBounds(string id)
    {
        Sta(() =>
        {
            var geometry=LabelLayout.Geometry(80,50);
            var label=LabelDocument.CreateLabel(new("1","Борщ с мясом свинина",145,SaleUnit.Piece),false,LabelTemplates.Find(id),layout:geometry);
            label.Measure(new Size(label.Width,label.Height)); label.Arrange(new Rect(0,0,label.Width,label.Height)); label.UpdateLayout();
            Assert.Equal(LabelDocument.Mm(80),label.ActualWidth,4); Assert.Equal(LabelDocument.Mm(50),label.ActualHeight,4);
            var blocks=LogicalChildren(label).OfType<TextBlock>().ToArray();
            Assert.Contains(blocks,b=>b.Text=="145,00 ₽");
            foreach(var block in blocks)
            {
                var bounds=block.TransformToAncestor(label).TransformBounds(new Rect(new Point(),block.RenderSize));
                Assert.True(bounds.Left>=-0.5 && bounds.Top>=-0.5 && bounds.Right<=label.ActualWidth+0.5 && bounds.Bottom<=label.ActualHeight+0.5,$"{id}: {block.Text}: {bounds}");
            }
            var document=LabelDocument.Create(LabelLayout.Create([new(new("1","Товар",145,SaleUnit.Piece),13)],geometry),false,layout:geometry);
            Assert.Equal(2,document.Pages.Count); Assert.Equal(geometry,LabelDocument.GeometryOf(document));
        });
    }
    [Theory]
    [InlineData("mint",1380,900)] [InlineData("indigo",1024,768)] [InlineData("graphite",640,480)]
    public void ResizingAndThemesKeepControlsReachableAndPresetsApply(string theme,double width,double height)
    {
        Sta(() =>
        {
            var settings=new AppSettings {ThemeId=theme}; settings.Normalize(); settings.SaveLabelPreset("Мой","chalkboard",80,50);
            var window=new MainWindow(settings,false) {Width=width,Height=height};
            try
            {
                window.AcceptCatalog(new ReferenceCatalog([new("3","Основной")],[
                    new("piece","Борщ с мясом свинина",new Dictionary<string,decimal>{{"3",145}},SaleUnit.Piece,MenuGroupId:"hot"),
                    new("weight","Каша молочная овсяная",new Dictionary<string,decimal>{{"3",350}},SaleUnit.Kilogram,MenuGroupId:"hot")
                ],[new("hot","Первые блюда")]),null);
                var products=(DataGrid)window.FindName("ProductsGrid");
                var productRows=((System.ComponentModel.ICollectionView)products.ItemsSource).Cast<ProductRow>().ToArray();
                foreach(var row in productRows) row.IsSelected=true;
                if(width>=1080) window.MinWidth=width;
                window.Show(); window.UpdateLayout();
                window.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Assert.Null(window.FindName("DemoButton")); Assert.Null(window.FindName("CertificatesButton"));
                var card=(Border)window.FindName("PrintCard");
                Assert.Equal(window.ActualWidth<1080?1:0,Grid.GetRow(card));
                var preview=(Button)window.FindName("PreviewButton"); var print=(Button)window.FindName("PrintButton");
                Assert.True(preview.IsVisible); Assert.True(print.IsVisible);
                Assert.True(preview.ActualWidth>150); Assert.True(print.ActualHeight>20);
                var expander=(SmoothExpander)window.FindName("LabelSettings"); Assert.False(expander.IsExpanded);
                expander.IsExpanded=true; PumpFor(TimeSpan.FromMilliseconds(260)); window.UpdateLayout();
                var body=LogicalChildren(expander).OfType<TextBox>().FirstOrDefault(); Assert.NotNull(body);
                Assert.True(body.IsVisible);
                expander.IsExpanded=false; PumpFor(TimeSpan.FromMilliseconds(260)); window.UpdateLayout();
                Assert.False(body.IsVisible);
                var widthBox=(TextBox)window.FindName("LabelWidthBox"); Assert.Equal("80",widthBox.Text);
                widthBox.Text="0"; Assert.False(preview.IsEnabled); Assert.False(print.IsEnabled);
                widthBox.Text="60"; Assert.True(preview.IsEnabled);
                ((TextBox)window.FindName("PresetNameBox")).Text="Новый";
                var save=LogicalChildren(window).OfType<Button>().Single(b=>Equals(b.Content,"Сохранить шаблон")); save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2,settings.LabelPresets.Count); Assert.Equal(60,settings.LabelPresets.Last().WidthMm);
                ((ComboBox)window.FindName("PresetBox")).SelectedValue=settings.LabelPresets.First().Id;
                Assert.Equal("80",widthBox.Text);
                Assert.Equal(theme,settings.ThemeId);
                if (Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is { } workspace)
                {
                    window.UpdateLayout();
                    var image=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);
                    image.Render(window);
                    var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                    var directory=System.IO.Path.Combine(workspace,"artifacts","ui"); System.IO.Directory.CreateDirectory(directory);
                    using var output=System.IO.File.Create(System.IO.Path.Combine(directory,$"{theme}.png")); encoder.Save(output);
                    print.BringIntoView(); PumpFor(TimeSpan.FromMilliseconds(100)); window.UpdateLayout();
                    var bounds=print.TransformToAncestor(window).TransformBounds(new Rect(new Point(),print.RenderSize));
                    Assert.True(bounds.Top>=0 && bounds.Bottom<=window.ActualHeight,$"Print action outside viewport: {bounds}");
                    var printImage=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); printImage.Render(window);
                    var printEncoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); printEncoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(printImage));
                    using var printOutput=System.IO.File.Create(System.IO.Path.Combine(directory,$"{theme}-print.png")); printEncoder.Save(printOutput);
                }

            }
            finally {window.Close();}
        });
    }

    [Fact]
    public void ExcessivelyLongNameIsRejectedRatherThanSilentlyTruncated()
    {
        Sta(() => Assert.Throws<ArgumentException>(() => LabelDocument.CreateLabel(
            new Product("1", string.Concat(Enumerable.Repeat("Очень длинное название ", 100)), 1m), false)));
    }

    private static IEnumerable<DependencyObject> LogicalChildren(DependencyObject item)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(item).OfType<DependencyObject>())
        {
            yield return child;
            foreach (var descendant in LogicalChildren(child)) yield return descendant;
        }
    }

    private static void PumpFor(TimeSpan duration)
    {
        var frame=new System.Windows.Threading.DispatcherFrame();
        var timer=new System.Windows.Threading.DispatcherTimer {Interval=duration};
        timer.Tick+=(_,_)=> {timer.Stop();frame.Continue=false;}; timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "WPF check timed out.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
