using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Kushat.Core;

namespace Kushat.App;

public static class LabelDocument
{
    public static double Mm(double mm) => mm * 96 / 25.4;

    public static FixedDocument Create(IReadOnlyList<LabelPage> pages, bool demo)
    {
        var document = new FixedDocument();
        var size = new Size(Mm(LabelLayout.PageWidthMm), Mm(LabelLayout.PageHeightMm));
        document.DocumentPaginator.PageSize = size;
        foreach (var page in pages)
        {
            var visual = new FixedPage { Width = size.Width, Height = size.Height, Background = Brushes.White };
            foreach (var slot in page.Slots)
            {
                var label = CreateLabel(slot.Product, demo);
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

    private static Border CreateLabel(Product product, bool demo)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = new TextBlock
        {
            Text = product.Name, FontSize = 30, FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        // Measure text before printing; never silently cut off a product name.
        var nameSize = new Size(Mm(84), Mm(demo ? 52 : 58));
        while (true)
        {
            name.Measure(new Size(nameSize.Width, double.PositiveInfinity));
            if (name.DesiredSize.Height <= nameSize.Height) break;
            if (name.FontSize <= 12)
                throw new ArgumentException("Наименование товара слишком длинное для ценника: " + product.Name);
            name.FontSize -= 1;
        }
        grid.Children.Add(name);
        AddPrice(grid, 1, "Цена за 1 кг", product.PricePerKg, 28);
        AddPrice(grid, 2, "Цена за 100 г", product.PricePer100Grams, 34);
        if (demo)
        {
            var note = new TextBlock { Text = "ДЕМОНСТРАЦИОННЫЕ ДАННЫЕ", FontSize = 11,
                Foreground = Brushes.Firebrick, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
            Grid.SetRow(note, 3); grid.Children.Add(note);
        }
        return new Border
        {
            Width = Mm(LabelLayout.LabelWidthMm), Height = Mm(LabelLayout.LabelHeightMm),
            BorderThickness = new Thickness(0.5), BorderBrush = Brushes.Gray,
            Padding = new Thickness(Mm(8)), Child = grid
        };
    }

    private static void AddPrice(Grid grid, int row, string title, decimal price, double fontSize)
    {
        var panel = new StackPanel { Margin = new Thickness(0, Mm(5), 0, 0) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 15,
            TextAlignment = TextAlignment.Center, Foreground = Brushes.DimGray });
        // Viewbox shrinks only an exceptionally long price, never the physical label.
        panel.Children.Add(new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
            Height = fontSize * 1.4, Child = new TextBlock {
                Text = price.ToString("N2", CultureInfo.GetCultureInfo("ru-RU")) + " ₽",
                FontSize = fontSize, FontWeight = FontWeights.Bold, TextAlignment = TextAlignment.Center } });
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
