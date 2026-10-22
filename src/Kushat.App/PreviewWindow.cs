using System.Windows;
using Kushat.Core;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;

namespace Kushat.App;

public sealed class PreviewWindow : Window
{
    public PreviewWindow(FixedDocument document, Action print)
    {
        Title = "Предварительный просмотр · А4 · масштаб печати 100%";
        Width = 860; Height = 850; MinWidth = 600; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel();
        var toolbar = new DockPanel { Margin = new Thickness(16) };
        var button = new Button { Content = "Напечатать…", HorizontalAlignment = HorizontalAlignment.Right };
        button.Click += (_, _) => print();
        DockPanel.SetDock(button, Dock.Right); toolbar.Children.Add(button);
        var geometry = LabelLayout.Geometry(document.DocumentPaginator.PageSize.Width > document.DocumentPaginator.PageSize.Height ? LabelOrientation.Landscape : LabelOrientation.Portrait);
        toolbar.Children.Add(new TextBlock { Text = $"Листов А4: {document.Pages.Count}. Размер ценника: {geometry.LabelWidthMm} × {geometry.LabelHeightMm} мм.",
            VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar);
        // No built-in print command: every print must pass printer geometry validation.
        var pages = new StackPanel();
        for (var i = 0; i < document.Pages.Count; i++)
        {
            var view = new DocumentPageView { DocumentPaginator = document.DocumentPaginator, PageNumber = i };
            var box = new Viewbox { Width = 640, Stretch = Stretch.Uniform, Child = view };
            pages.Children.Add(new Border { Child = box, Background = Brushes.White,
                BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Margin = new Thickness(20, 0, 20, 20),
                HorizontalAlignment = HorizontalAlignment.Center });
        }
        root.Children.Add(new ScrollViewer { Content = pages, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }
}
