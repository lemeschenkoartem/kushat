using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Kushat.Core;

namespace Kushat.App;

public sealed class LoginWindow : Window
{
    public ReferenceConnection Connection { get; private set; }
    public ReferenceCatalog? Catalog { get; private set; }
    public string Password { get; private set; } = "";
    public string ProfileName { get; private set; } = "";
    public bool RememberLogin { get; private set; }
    public ConnectionReport? ServerReport { get; private set; }

    public LoginWindow(AppSettings saved, HttpClient http)
    {
        Connection = saved.Connection;
        Title = $"Вход в R-Keeper · {App.DisplayVersion}"; Width = 560; SizeToContent = SizeToContent.Height;
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 40);
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "Подключение к R-Keeper", FontSize = 23,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 20) });
        var profiles = new ComboBox { ItemsSource = saved.Profiles, DisplayMemberPath = nameof(ConnectionProfile.DisplayName),
            MinHeight = 34, MaxDropDownHeight = 180 };
        AddField(panel, "Сохранённые подключения", profiles);
        var newProfile = new Button { Content = "Новое подключение", HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(newProfile);
        var name = new TextBox();
        AddField(panel, "Название подключения (необязательно)", name);
        var address = new TextBox { ToolTip = "http://сервер:порт или https://сервер:порт. Без протокола используется HTTP." };
        AddField(panel, "Адрес сервера с портом", address);
        var login = new TextBox();
        AddField(panel, "Название работника R-Keeper", login);
        var password = new PasswordBox { Padding = new Thickness(8), MinHeight = 34 };
        AddField(panel, "Пароль", password);
        var remember = new CheckBox { Content = "Запомнить вход на этом компьютере", IsChecked = true, Margin = new Thickness(0, 0, 0, 10) };
        panel.Children.Add(remember);
        panel.Children.Add(new TextBlock { Text = "Для каждого объекта укажите его адрес и порт. Сохранённый пароль защищён Windows и доступен этой учётной записи на этом компьютере.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16), Foreground = System.Windows.Media.Brushes.DimGray });
        var checkButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        var checkServer = new Button { Content = "Проверить порт и сертификат" };
        var certificates = new Button { Content = "Мастер сертификата…" };
        var importCertificate = new Button { Content = "Доверие из .cer…" };
        checkButtons.Children.Add(checkServer); checkButtons.Children.Add(certificates); checkButtons.Children.Add(importCertificate); panel.Children.Add(checkButtons);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) };
        panel.Children.Add(new ScrollViewer { Content = status, MaxHeight = 150, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
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

        void Fill(ConnectionProfile? profile)
        {
            name.Text = profile?.Name ?? "";
            address.Text = profile?.Connection.ServerAddress ?? "";
            login.Text = profile?.Connection.UserName ?? "";
            remember.IsChecked = profile?.RememberLogin ?? true;
            password.Clear(); status.Text = ""; copyError.Visibility = Visibility.Collapsed;
            if (profile?.SavedLogin is null) return;
            try { password.Password = profile.SavedLogin.Open(profile.Connection, new WindowsLoginProtector()); }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
            { status.Text = "Не удалось восстановить сохранённый вход. Введите пароль заново."; }
        }
        profiles.SelectionChanged += (_, _) => Fill(profiles.SelectedItem as ConnectionProfile);
        newProfile.Click += (_, _) => { profiles.SelectedIndex = -1; Fill(null); name.Focus(); };
        profiles.SelectedItem = saved.ActiveProfile;
        if (saved.ActiveProfile is null) { address.Text = saved.Connection.ServerAddress; login.Text = saved.Connection.UserName; }
        // Never send an autofilled password to an edited endpoint or a different employee.
        address.TextChanged += (_, _) => password.Clear();
        login.TextChanged += (_, _) => password.Clear();

        CancellationTokenSource? pending = null;
        var closeRequested = false;
        certificates.Click += (_, _) => CertificateToolsLauncher.OpenFrom(this);
        importCertificate.Click += async (_, _) =>
        {
            if (pending is not null) return;
            pending = new CancellationTokenSource();
            connect.IsEnabled = checkServer.IsEnabled = certificates.IsEnabled = importCertificate.IsEnabled = address.IsEnabled = profiles.IsEnabled =
                login.IsEnabled = password.IsEnabled = newProfile.IsEnabled = name.IsEnabled = remember.IsEnabled = false;
            try
            {
                var report = await CertificateClientTrust.ImportAsync(this, address.Text, pending.Token);
                if (report is not null) { ServerReport = report; status.Text = report.Text + "\nДоверие настроено."; }
            }
            catch (OperationCanceledException) { status.Text = "Проверка отменена."; }
            catch (Exception ex) when (ex is ArgumentException or System.IO.IOException or CryptographicException or UnauthorizedAccessException or InvalidOperationException)
            { status.Text = ConnectionDiagnostics.Describe(ex); copyError.Visibility = Visibility.Visible; }
            finally
            {
                pending.Dispose(); pending = null;
                connect.IsEnabled = checkServer.IsEnabled = certificates.IsEnabled = importCertificate.IsEnabled = address.IsEnabled = profiles.IsEnabled =
                    login.IsEnabled = password.IsEnabled = newProfile.IsEnabled = name.IsEnabled = remember.IsEnabled = true;
                if (closeRequested) { password.Clear(); DialogResult = false; }
            }
        };
        checkServer.Click += async (_, _) =>
        {
            if (pending is not null) return;
            pending = new CancellationTokenSource();
            connect.IsEnabled = checkServer.IsEnabled = certificates.IsEnabled = importCertificate.IsEnabled = address.IsEnabled = profiles.IsEnabled =
                login.IsEnabled = password.IsEnabled = newProfile.IsEnabled = name.IsEnabled = remember.IsEnabled = false;
            status.Text = "Проверяем доступность порта и сертификат…";
            try
            {
                var endpoint = new ReferenceConnection { ServerAddress = address.Text }.GetServerEndpoint();
                ServerReport = await new ConnectionPreflight().CheckAsync(endpoint, 10, pending.Token);
                status.Text = ServerReport.Text; copyError.Visibility = Visibility.Visible;
            }
            catch (OperationCanceledException) { status.Text = "Проверка отменена."; }
            catch (Exception ex) when (ex is ArgumentException or System.IO.IOException) { status.Text = ConnectionDiagnostics.Describe(ex); }
            finally
            {
                pending.Dispose(); pending = null;
                connect.IsEnabled = checkServer.IsEnabled = certificates.IsEnabled = importCertificate.IsEnabled = address.IsEnabled = profiles.IsEnabled =
                    login.IsEnabled = password.IsEnabled = newProfile.IsEnabled = name.IsEnabled = remember.IsEnabled = true;
                if (closeRequested) { password.Clear(); DialogResult = false; }
            }
        };
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
            connect.IsEnabled = checkServer.IsEnabled = certificates.IsEnabled = importCertificate.IsEnabled = address.IsEnabled = login.IsEnabled = password.IsEnabled =
                profiles.IsEnabled = newProfile.IsEnabled = name.IsEnabled = remember.IsEnabled = false;
            status.Text = "Проверка входа и чтение меню…"; copyError.Visibility = Visibility.Collapsed;
            var success = false;
            try
            {
                var connection = new ReferenceConnection { ServerAddress = address.Text.Trim(), UserName = login.Text.Trim(),
                    TimeoutSeconds = saved.Connection.TimeoutSeconds };
                var catalog = await new ReferenceConnectionWorkflow(http).ConnectAsync(connection, password.Password,
                    result => { ServerReport = result; status.Text = result.Text + "\nПроверка входа и чтение меню…"; }, pending.Token);
                Connection = connection; Catalog = catalog; Password = password.Password;
                ProfileName = name.Text.Trim(); RememberLogin = remember.IsChecked == true; success = true;
            }
            catch (OperationCanceledException)
            {
                status.Text = pending.IsCancellationRequested ? "Вход отменён." : $"TIMEOUT: Сервер не ответил за {saved.Connection.TimeoutSeconds} секунд.";
                copyError.Visibility = Visibility.Visible;
            }
            catch (Exception ex) when (ex is HttpRequestException or System.IO.IOException or FormatException or ArgumentException)
            {
                status.Text = ConnectionDiagnostics.Describe(ex); copyError.Visibility = Visibility.Visible;
                password.Clear(); password.Focus();
            }
            finally
            {
                pending.Dispose(); pending = null;
                connect.IsEnabled = checkServer.IsEnabled = certificates.IsEnabled = importCertificate.IsEnabled = address.IsEnabled = login.IsEnabled = password.IsEnabled =
                    profiles.IsEnabled = newProfile.IsEnabled = name.IsEnabled = remember.IsEnabled = true;
                if (closeRequested) { Password = ""; Catalog = null; password.Clear(); DialogResult = false; }
                else if (success) { password.Clear(); DialogResult = true; }
            }
        };
    }

    private static void AddField(Panel panel, string label, Control input)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 6) });
        input.Margin = new Thickness(0, 0, 0, 14); panel.Children.Add(input);
    }
}
