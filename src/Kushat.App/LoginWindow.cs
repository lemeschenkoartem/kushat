using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using Kushat.Core;

namespace Kushat.App;

public sealed class LoginWindow : Window
{
    public ReferenceConnection Connection { get; private set; }
    public ReferenceCatalog? Catalog { get; private set; }
    public string Password { get; private set; } = "";

    public LoginWindow(ReferenceConnection saved, HttpClient http)
    {
        Connection = saved;
        Title = $"Вход в R-Keeper · {App.DisplayVersion} · HTTP/HTTPS"; Width = 520; SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 40);
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "Подключение к R-Keeper", FontSize = 23, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) });
        var address = new TextBox { Text = saved.ServerAddress, ToolTip = "Адрес XML-интерфейса R-Keeper: http://сервер:порт или https://сервер:порт. Без протокола используется HTTP." };
        AddField(panel, "Адрес сервера с портом", address);
        var login = new TextBox { Text = saved.UserName };
        AddField(panel, "Логин сотрудника", login);
        var password = new PasswordBox { Padding = new Thickness(8), MinHeight = 34 };
        AddField(panel, "Пароль", password);
        panel.Children.Add(new TextBlock { Text = "Для локального подключения можно указать http://сервер:порт. Без протокола используется HTTP. Порт указывает администратор сервера. Пароль не сохраняется.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 16), Foreground = System.Windows.Media.Brushes.DimGray });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(new ScrollViewer { Content = status, MaxHeight = 190,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var copyError = new Button { Content = "Скопировать ошибку", Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
        copyError.Click += (_, _) =>
        {
            try { Clipboard.SetText(status.Text); }
            catch (System.Runtime.InteropServices.ExternalException)
            { status.Text += "\nНе удалось записать текст в буфер обмена. Попробуйте ещё раз."; }
        };
        panel.Children.Add(copyError);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var connect = new Button { Content = "Войти и загрузить меню", IsDefault = true };
        var cancel = new Button { Content = "Отмена" };
        buttons.Children.Add(connect); buttons.Children.Add(cancel); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        CancellationTokenSource? pending = null;
        var closeRequested = false;
        Closing += (_, e) => { if (pending is not null) { e.Cancel = true; closeRequested = true; pending.Cancel(); } };
        cancel.Click += (_, _) =>
        {
            if (pending is not null) { closeRequested = true; pending.Cancel(); }
            else { password.Clear(); DialogResult = false; }
        };
        connect.Click += async (_, _) =>
        {
            if (pending is not null) return;
            pending = new CancellationTokenSource();
            connect.IsEnabled = address.IsEnabled = login.IsEnabled = password.IsEnabled = false;
            status.Text = "Проверка входа и чтение меню…";
            copyError.Visibility = Visibility.Collapsed;
            var success = false;
            try
            {
                var connection = new ReferenceConnection { ServerAddress = address.Text.Trim(), UserName = login.Text.Trim(), TimeoutSeconds = saved.TimeoutSeconds };
                var catalog = await new ReferenceServerClient(http).ConnectAsync(connection, password.Password, pending.Token);
                Connection = connection; Catalog = catalog; Password = password.Password; success = true;
            }
            catch (OperationCanceledException)
            {
                status.Text = pending.IsCancellationRequested ? "Вход отменён." : $"TIMEOUT: Сервер не ответил за {saved.TimeoutSeconds} секунд.";
                copyError.Visibility = Visibility.Visible;
            }
            catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or FormatException or ArgumentException)
            {
                status.Text = ConnectionDiagnostics.Describe(ex);
                copyError.Visibility = Visibility.Visible;
                password.Clear(); password.Focus();
            }
            finally
            {
                pending.Dispose(); pending = null;
                connect.IsEnabled = address.IsEnabled = login.IsEnabled = password.IsEnabled = true;
                if (closeRequested) { Password = ""; Catalog = null; password.Clear(); DialogResult = false; }
                else if (success) { password.Clear(); DialogResult = true; }
            }
        };
    }

    private static void AddField(Panel panel, string label, Control input)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        input.Margin = new Thickness(0, 0, 0, 16); panel.Children.Add(input);
    }
}
