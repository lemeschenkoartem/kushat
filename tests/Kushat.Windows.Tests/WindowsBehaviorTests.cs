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
    public void EveryTemplateShowsBothCorrectPricesAndDemoMarkWithinFixedDimensions(string id)
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
            Assert.Contains(text, t => t.Text == "315,55 ₽");
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

    public static IEnumerable<object[]> TemplatePriceModes() =>
        from template in LabelTemplates.All
        from mode in Enum.GetValues<WeightPriceMode>()
        from unit in new[] { SaleUnit.Kilogram, SaleUnit.Piece }
        select new object[] { template.Id, mode, unit };

    [Theory]
    [MemberData(nameof(TemplatePriceModes))]
    public void RenderedLabelsRespectWeightChoiceAndNeverInventWeightPricesForPieceGoods(string id, WeightPriceMode mode, SaleUnit unit)
    {
        Sta(() =>
        {
            var label = LabelDocument.CreateLabel(new Product("1", "Товар", 55m, unit), false, LabelTemplates.Find(id), mode);
            label.Measure(new Size(label.Width, label.Height));
            label.Arrange(new Rect(0, 0, label.Width, label.Height));
            var text = LogicalChildren(label).OfType<TextBlock>().Select(t => t.Text).ToArray();
            if (unit == SaleUnit.Piece)
            {
                Assert.Contains("Цена за 1 шт.", text);
                Assert.DoesNotContain("Цена за 100 г", text);
                Assert.DoesNotContain("Цена за 1 кг", text);
            }
            else
            {
                Assert.Equal(mode != WeightPriceMode.HundredGrams, text.Contains("Цена за 1 кг"));
                Assert.Equal(mode != WeightPriceMode.Kilogram, text.Contains("Цена за 100 г"));
            }
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
