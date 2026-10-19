using System.Globalization;
using System.Windows;
using System.Windows.Markup;

namespace Kushat.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage("ru-RU")));
        base.OnStartup(e);
    }
}
