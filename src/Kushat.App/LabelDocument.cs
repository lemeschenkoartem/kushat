using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Kushat.Core;

namespace Kushat.App;

public static class LabelDocument
{
    public static double Mm(double mm) => mm * 96 / 25.4;

    public static FixedDocument Create(IReadOnlyList<LabelPage> pages, bool demo, LabelTemplate? template = null, WeightPriceMode weightPriceMode = WeightPriceMode.Both)
    {
        var document = new FixedDocument();
        var size = new Size(Mm(LabelLayout.PageWidthMm), Mm(LabelLayout.PageHeightMm));
        document.DocumentPaginator.PageSize = size;
        foreach (var page in pages)
        {
            var visual = new FixedPage { Width = size.Width, Height = size.Height, Background = Brushes.White };
            foreach (var slot in page.Slots)
            {
                var label = CreateLabel(slot.Product, demo, template, weightPriceMode);
                FixedPage.SetLeft(label, Mm(LabelLayout.LeftMm + slot.Column * LabelLayout.LabelWidthMm));
                FixedPage.SetTop(label, Mm(LabelLayout.TopMm + slot.Row * LabelLayout.LabelHeightMm));
                visual.Children.Add(label);
            }
            var content = new PageContent();
            ((System.Windows.Markup.IAddChild)content).AddChild(visual);
            document.Pages.Add(content);
        }
        return document;
    }

    public static Border CreateLabel(Product product, bool demo, LabelTemplate? template = null, WeightPriceMode weightPriceMode = WeightPriceMode.Both)
    {
        if (!product.CanPrint) throw new ArgumentException("Не определена единица товара: " + product.Name);
        template ??= LabelTemplates.All[0];
        var prices = LabelPrices.For(product, weightPriceMode, template.Style == LabelStyle.LargeHundredGrams);
        var accent = new SolidColorBrush(Color.FromRgb(33, 92, 69));
        var chalk = template.Style == LabelStyle.Chalkboard;
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBlock
        {
            Text = product.Name, FontSize = 30, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = chalk ? Brushes.White : Brushes.Black,
            FontFamily = chalk ? new FontFamily("Georgia") : new FontFamily("Segoe UI"),
            FontStyle = chalk ? FontStyles.Italic : FontStyles.Normal,
            TextAlignment = template.Style == LabelStyle.Accent ? TextAlignment.Left : TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        // Fit the complete name into a reserved area; no ellipsis or silent clipping.
        var nameSize = new Size(Mm(84) - 1, Mm(48));
        while (true)
        {
            name.Measure(new Size(nameSize.Width, double.PositiveInfinity));
            if (name.DesiredSize.Height <= nameSize.Height) break;
            if (name.FontSize <= 12)
                throw new ArgumentException("Наименование товара слишком длинное для ценника: " + product.Name);
            name.FontSize -= 1;
        }
        Grid.SetRow(name, 1); grid.Children.Add(name);
        switch (template.Style)
        {
            case LabelStyle.Classic:
                AddPrice(grid, 2, prices.Primary.Title, prices.Primary.Amount, prices.Secondary is not null ? 28 : 48);
                if (prices.Secondary is { } classicSecondary) AddPrice(grid, 3, classicSecondary.Title, classicSecondary.Amount, 34);
                break;
            case LabelStyle.LargeHundredGrams:
                var price = PricePanel(prices.Primary.Title, prices.Primary.Amount, 64);
                var highlight = new Border { Background = new SolidColorBrush(Color.FromRgb(238, 246, 240)),
                    CornerRadius = new CornerRadius(7), Padding = new Thickness(Mm(3)), Child = price };
                Grid.SetRow(highlight, 2); grid.Children.Add(highlight);
                if (prices.Secondary is { } largeSecondary) AddPrice(grid, 3, largeSecondary.Title, largeSecondary.Amount, 24);
                break;
            case LabelStyle.Accent:
                var stripe = new Border { Background = accent, Padding = new Thickness(10, 5, 10, 5),
                    Margin = new Thickness(0, 0, 0, Mm(4)), Child = new TextBlock { Text = "ЦЕННИК", FontSize = 14,
                        FontWeight = FontWeights.SemiBold, Foreground = Brushes.White } };
                grid.Children.Add(stripe);
                AddPrice(grid, 2, prices.Primary.Title, prices.Primary.Amount, 42);
                if (prices.Secondary is { } accentSecondary) AddPrice(grid, 3, accentSecondary.Title, accentSecondary.Amount, 28);
                break;
            case LabelStyle.Chalkboard:
                AddPrice(grid, 2, prices.Primary.Title, prices.Primary.Amount, prices.Secondary is not null ? 34 : 56, Brushes.White, new FontFamily("Georgia"));
                if (prices.Secondary is { } chalkSecondary) AddPrice(grid, 3, chalkSecondary.Title, chalkSecondary.Amount, 40, Brushes.White, new FontFamily("Georgia"));
                break;
            case LabelStyle.Cafe:
                var awning = new UniformGrid { Rows = 1, Columns = 14, Height = Mm(9) };
                for (var i = 0; i < 14; i++)
                    awning.Children.Add(new Border { Background = i % 2 == 0 ? Brushes.Black : Brushes.LightGray,
                        CornerRadius = new CornerRadius(0, 0, 5, 5) });
                var header = new StackPanel { Margin = new Thickness(0, 0, 0, Mm(4)) };
                header.Children.Add(awning);
                header.Children.Add(new TextBlock { Text = "ЦЕННИК", FontSize = 18, FontWeight = FontWeights.SemiBold,
                    TextAlignment = TextAlignment.Center, Margin = new Thickness(0, Mm(3), 0, 0) });
                grid.Children.Add(header);
                AddPrice(grid, 2, prices.Primary.Title, prices.Primary.Amount, prices.Secondary is not null ? 36 : 48);
                if (prices.Secondary is { } cafeSecondary) AddPrice(grid, 3, cafeSecondary.Title, cafeSecondary.Amount, 32);
                break;
            default: throw new ArgumentException("Неизвестный шаблон ценника.");
        }
        if (demo)
        {
            var note = new TextBlock { Text = "ДЕМОНСТРАЦИОННЫЕ ДАННЫЕ", FontSize = 11,
                Foreground = chalk ? Brushes.White : Brushes.Firebrick, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
            Grid.SetRow(note, 4); grid.Children.Add(note);
        }
        return new Border
        {
            Width = Mm(LabelLayout.LabelWidthMm), Height = Mm(LabelLayout.LabelHeightMm),
            Background = chalk ? new SolidColorBrush(Color.FromRgb(22, 22, 22)) : Brushes.White,
            CornerRadius = chalk ? new CornerRadius(Mm(3)) : new CornerRadius(0),
            BorderThickness = new Thickness(template.Style == LabelStyle.Cafe ? 1.2 : 0.5),
            BorderBrush = chalk ? Brushes.Black : template.Style == LabelStyle.Cafe ? Brushes.Black : Brushes.Gray,
            Padding = new Thickness(Mm(8)), Child = grid
        };
    }

    private static StackPanel PricePanel(string title, decimal price, double fontSize, Brush? foreground = null, FontFamily? family = null)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15,
            TextAlignment = TextAlignment.Center, Foreground = foreground ?? Brushes.DimGray });
        // Shrink unusually long prices, keeping the physical label size unchanged.
        panel.Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            Height = fontSize * 1.4, Child = new TextBlock {
                Text = price.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) + " ₽",
                FontSize = fontSize, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center,
                Foreground = foreground ?? Brushes.Black, FontFamily = family ?? new FontFamily("Segoe UI"),
                FontStyle = family is null ? FontStyles.Normal : FontStyles.Italic } });
        return panel;
    }

    private static void AddPrice(Grid grid, int row, string title, decimal price, double fontSize, Brush? foreground = null, FontFamily? family = null)
    {
        var panel = PricePanel(title, price, fontSize, foreground, family);
        panel.Margin = new Thickness(0, Mm(5), 0, 0);
        Grid.SetRow(panel, row); grid.Children.Add(panel);
    }

    public static void Print(FixedDocument document, string printerName)
    {
        using var server = new LocalPrintServer();
        using var queues = server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]);
        using var queue = queues.FirstOrDefault(q => q.FullName == printerName)
            ?? throw new InvalidOperationException("Выбранный принтер больше не установлен. Обновите список принтеров.");
        var ticket = queue.DefaultPrintTicket ?? new PrintTicket();
        ticket.PageMediaSize = new PageMediaSize(PageMediaSizeName.ISOA4);
        ticket.PageOrientation = PageOrientation.Portrait;
        ticket.PageScalingFactor = 100;
        ticket.CopyCount = 1;
        ticket.PagesPerSheet = 1;
        ticket.Duplexing = Duplexing.OneSided;
        var validated = queue.MergeAndValidatePrintTicket(queue.DefaultPrintTicket, ticket).ValidatedPrintTicket;
        if (validated.PageMediaSize?.PageMediaSizeName != PageMediaSizeName.ISOA4 ||
            validated.PageOrientation != PageOrientation.Portrait ||
            validated.PageScalingFactor is not (null or 100) ||
            validated.PagesPerSheet is not (null or 1) || validated.CopyCount is not (null or 1) ||
            validated.Duplexing is not (null or Duplexing.OneSided))
            throw new InvalidOperationException("Драйвер не подтвердил А4, книжную ориентацию и масштаб 100%. Проверьте параметры принтера.");
        var area = queue.GetPrintCapabilities(validated).PageImageableArea
            ?? throw new InvalidOperationException("Драйвер принтера не сообщил область печати.");
        const double toMm = 25.4 / 96;
        if (!LabelLayout.FitsImageableArea(area.OriginWidth * toMm, area.OriginHeight * toMm,
            area.ExtentWidth * toMm, area.ExtentHeight * toMm))
            throw new InvalidOperationException("Поля этого принтера не позволяют разместить четыре ценника 100 × 140 мм на А4. Нужны поля слева/справа не более 5 мм и сверху/снизу не более 8,5 мм. Выберите другой принтер или режим печати А4 с меньшими полями.");
        var dialog = new PrintDialog { PrintQueue = queue, PrintTicket = validated };
        dialog.PrintDocument(document.DocumentPaginator, "Кушать — ценники 100 × 140 мм");
    }
}
