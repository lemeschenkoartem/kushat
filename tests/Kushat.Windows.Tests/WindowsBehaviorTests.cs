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
            var window = new MainWindow();
            try
            {
                ((Button)window.FindName("DemoButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
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
