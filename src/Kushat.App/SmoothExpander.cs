using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
namespace Kushat.App;
public sealed class SmoothExpander : Expander
{
    private Border? reveal;
    private ContentPresenter? body;
    private int revision;
    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        reveal = GetTemplateChild("PART_Reveal") as Border;
        body = GetTemplateChild("PART_Body") as ContentPresenter;
        if (body is not null) body.Visibility = IsExpanded ? Visibility.Visible : Visibility.Collapsed;
        if (reveal is not null) reveal.Height = IsExpanded ? double.NaN : 0;
    }
    protected override void OnExpanded() { base.OnExpanded(); Animate(true); }
    protected override void OnCollapsed() { base.OnCollapsed(); Animate(false); }
    private void Animate(bool expanded)
    {
        if (reveal is null || body is null) return;
        var token = ++revision;
        var current = reveal.ActualHeight;
        body.Visibility = Visibility.Visible; body.IsEnabled = expanded;
        body.Measure(new Size(Math.Max(100, ActualWidth), double.PositiveInfinity));
        var target = expanded ? body.DesiredSize.Height : 0;
        reveal.BeginAnimation(HeightProperty, null); reveal.Height = current;
        var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 210 : 0))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        animation.Completed += (_, _) =>
        {
            if (token != revision) return;
            reveal.BeginAnimation(HeightProperty, null); reveal.Height = expanded ? double.NaN : 0;
            body.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        };
        reveal.BeginAnimation(HeightProperty, animation);
    }
}
