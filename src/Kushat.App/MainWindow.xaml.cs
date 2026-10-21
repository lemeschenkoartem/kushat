using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Printing;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using Kushat.Core;

namespace Kushat.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<ProductRow> rows = [];
    private readonly ICollectionView view;
    private readonly HttpClient http = ReferenceHttpClient.Create();
    private readonly WindowsLoginProtector loginProtector = new();
    private AppSettings settings = new();
    private string credential = "";
    private bool demo;
    private DateTime? loadedAt;
    private DateTime? catalogLoadedAt;
    private ReferenceCatalog? catalog;
    private string? priceTypeId;
    private bool changingPriceType;
    private bool settingsWritable;
    private CancellationTokenSource? loading;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"Кушать · Печать ценников · {App.DisplayVersion}";
        view = CollectionViewSource.GetDefaultView(rows);
        view.Filter = item => item is ProductRow row &&
            (row.Name.Contains(SearchBox.Text.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
             row.Id.Contains(SearchBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        ProductsGrid.ItemsSource = view;
        try { settings = SettingsStore.Load(); settingsWritable = true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        { ShowError("Не удалось прочитать настройки. Их можно задать заново. Файл: " + SettingsStore.FilePath); }
        TemplateBox.ItemsSource = LabelTemplates.All;
        TemplateBox.SelectedValue = settings.TemplateId;
        WeightPriceBox.Items.Add(new ComboBoxItem { Content = "За 1 кг и за 100 г", Tag = WeightPriceMode.Both });
        WeightPriceBox.Items.Add(new ComboBoxItem { Content = "Только за 1 кг", Tag = WeightPriceMode.Kilogram });
        WeightPriceBox.Items.Add(new ComboBoxItem { Content = "Только за 100 г", Tag = WeightPriceMode.HundredGrams });
        WeightPriceBox.SelectedValue = settings.WeightPriceMode;
        LoadPrinters(); UpdateSummary();
        Loaded += AutoConnectLoaded;
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) => view?.Refresh();

    private void ReplaceProducts(IEnumerable<Product> products, bool isDemo, DateTime? sourceTime = null)
    {
        foreach (var row in rows) row.PropertyChanged -= RowChanged;
        rows.Clear();
        foreach (var product in products.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = new ProductRow(product);
            row.PropertyChanged += RowChanged; rows.Add(row);
        }
        demo = isDemo; loadedAt = sourceTime ?? DateTime.Now; UpdateSummary();
    }

    private bool ConfirmReplace() => !rows.Any(r => r.IsSelected) || MessageBox.Show(this,
        "Загрузка нового списка сбросит выбранные товары и количества. Продолжить?", "Обновление товаров",
        MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private async void RefreshClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(credential)) { ConnectClick(sender, e); return; }
        if (loading is not null || !ConfirmReplace()) return;
        await ReloadCatalogAsync(false);
    }

    private async void AutoConnectLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= AutoConnectLoaded;
        var profile = settings.ActiveProfile;
        if (profile?.SavedLogin is null || !profile.RememberLogin) return;
        try { credential = profile.SavedLogin.Open(profile.Connection, loginProtector); }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            ForgetSavedLogin();
            StatusText.Text = "Не удалось восстановить сохранённый вход. Нажмите «Подключения…» и введите пароль заново.";
            return;
        }
        await ReloadCatalogAsync(true);
    }

    private async Task ReloadCatalogAsync(bool automatic)
    {
        loading = new CancellationTokenSource();
        SetBusy(true); StatusText.Text = automatic ? "Автоматический вход и загрузка свежих цен…" : "Загрузка меню из реф-сервера…";
        try
        {
            var result = await new ReferenceServerClient(http).ConnectAsync(settings.Connection, credential, loading.Token);
            AcceptCatalog(result, automatic ? null : priceTypeId);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Загрузка отменена или истёк таймаут. Предыдущий список сохранён; его цены не обновлены.";
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException or ArgumentException)
        {
            if (ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden })
            { ForgetSavedLogin(); ClearSession(); }
            var reason = ConnectionDiagnostics.Describe(ex);
            StatusText.Text = "Товары не обновлены. " + reason;
            if (!automatic) ShowError(StatusText.Text);
        }
        finally { loading.Dispose(); loading = null; SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        RefreshButton.IsEnabled = DemoButton.IsEnabled = ProductsGrid.IsEnabled =
            ConnectButton.IsEnabled = PriceTypeBox.IsEnabled = SelectButton.IsEnabled = ClearButton.IsEnabled =
            PreviewButton.IsEnabled = PrintButton.IsEnabled = !busy;
        LogoutButton.IsEnabled = !busy && !string.IsNullOrEmpty(credential);
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => loading?.Cancel();

    private void DemoClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmReplace()) return;
        ClearSession();
        ReplaceProducts([
            new("demo-1", "Салат Оливье", 850m), new("demo-2", "Плов с говядиной", 650m),
            new("demo-3", "Пирожок с капустой", 55m, SaleUnit.Piece), new("demo-4", "Картофельное пюре", 315.55m),
            new("demo-5", "Салат из свежих овощей с зеленью", 420m)
        ], true);
        StatusText.Text = "ДЕМО: товары и цены вымышлены. На каждом ценнике будет отметка «Демонстрационные данные».";
    }

    private void RowChanged(object? sender, PropertyChangedEventArgs e) => UpdateSummary();

    private void UpdateSummary()
    {
        var selected = rows.Where(r => r.IsSelected).ToArray();
        if (selected.Any(r => !r.TryQuantity(out _)))
        { SummaryText.Text = "Исправьте количество у выбранных товаров: целое число от 1 до 999."; return; }
        var count = selected.Sum(r => { r.TryQuantity(out var n); return n; });
        SummaryText.Text = $"Выбрано товаров: {selected.Length}\nЦенников: {count}\nЛистов А4: {(count + 3) / 4}" +
            (count > LabelLayout.MaxLabels ? "\nМаксимум 1000 ценников за раз." : "");
    }

    private void SelectVisibleClick(object sender, RoutedEventArgs e)
    { foreach (var row in view.Cast<ProductRow>().Where(r => r.CanPrint).ToArray()) row.IsSelected = true; }
    private void ClearSelectionClick(object sender, RoutedEventArgs e)
    { foreach (var row in rows) row.IsSelected = false; }

    private void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (loading is not null || !ConfirmReplace()) return;
        var dialog = new LoginWindow(settings, http) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        credential = dialog.Password;
        LoginEnvelope? savedLogin = null;
        if (dialog.RememberLogin)
        {
            try { savedLogin = LoginEnvelope.Create(dialog.Connection, credential, loginProtector); }
            catch (CryptographicException)
            { ShowError("Вход выполнен, но Windows не удалось защитить пароль. Автоматический вход не сохранён."); }
        }
        var profile = settings.RememberConnection(dialog.Connection, dialog.ProfileName, savedLogin);
        profile.RememberLogin = dialog.RememberLogin;
        settingsWritable = true;
        SaveSettings();
        LogoutButton.IsEnabled = true;
        AcceptCatalog(dialog.Catalog!, null);
    }

    private void AcceptCatalog(ReferenceCatalog result, string? selectedType)
    {
        catalog = result; catalogLoadedAt = DateTime.Now; priceTypeId = null;
        ConnectionText.Text = $"Сервер: {settings.Connection.ServerAddress} · работник: {settings.Connection.UserName}";
        ReplaceProducts([], false);
        changingPriceType = true;
        PriceTypeBox.ItemsSource = result.PriceTypes;
        PriceTypeBox.SelectedValue = result.PriceTypes.Any(p => p.Id == selectedType) ? selectedType :
            result.DefaultPriceTypeId;
        changingPriceType = false;
        PriceTypePanel.Visibility = Visibility.Visible;
        if (PriceTypeBox.SelectedValue is string id) ApplyPriceType(id);
        else StatusText.Text = $"Вход выполнен. Получено позиций: {result.Items.Count}. Выберите тип цены.";
    }

    private void PriceTypeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (changingPriceType || catalog is null || PriceTypeBox.SelectedValue is not string id) return;
        if (!ConfirmReplace())
        {
            changingPriceType = true; PriceTypeBox.SelectedValue = priceTypeId; changingPriceType = false; return;
        }
        ApplyPriceType(id);
    }

    private void ApplyPriceType(string id)
    {
        try
        {
            var priced = catalog!.SelectPriceType(id);
            priceTypeId = id;
            ReplaceProducts(priced.Products, false, catalogLoadedAt);
            StatusText.Text = $"Загружено товаров: {rows.Count}. Без цены и недоступно для печати: {priced.WithoutPrice}. Обновлено: {loadedAt:dd.MM.yyyy HH:mm}. Цена — за кг или штуку по данным R-Keeper. Не определена единица: {rows.Count(r => !r.CanPrint)}; эти товары недоступны для печати.";
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            priceTypeId = id; ReplaceProducts([], false); StatusText.Text = ex.Message; ShowError(ex.Message);
        }
    }

    private void LogoutClick(object sender, RoutedEventArgs e)
    {
        if (!ConfirmReplace()) return;
        var forgotten = ForgetSavedLogin(); ClearSession();
        StatusText.Text = forgotten ? "Вы вышли из R-Keeper. Сохранённый вход для этого подключения удалён."
            : "Вы вышли из текущего сеанса, но не удалось удалить сохранённый вход из файла настроек.";
    }

    private bool ForgetSavedLogin()
    {
        if (settings.ActiveProfile is not { } profile) return true;
        profile.SavedLogin = null; profile.RememberLogin = false; return SaveSettings();
    }

    private void ClearSession()
    {
        credential = ""; catalog = null; catalogLoadedAt = null; priceTypeId = null;
        ConnectionText.Text = "Нет активного подключения";
        changingPriceType = true; PriceTypeBox.ItemsSource = null; changingPriceType = false;
        PriceTypePanel.Visibility = Visibility.Collapsed; LogoutButton.IsEnabled = false;
        ReplaceProducts([], false); loadedAt = null;
    }

    private void LoadPrinters()
    {
        try
        {
            using var server = new LocalPrintServer();
            using var queues = server.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections]);
            var names = queues.Select(q => q.FullName).Distinct().OrderBy(n => n).ToArray();
            var saved = settings.PrinterName;
            PrinterBox.ItemsSource = names;
            var defaultName = server.DefaultPrintQueue?.FullName;
            PrinterBox.SelectedItem = names.Contains(saved) ? saved : names.Contains(defaultName) ? defaultName : names.FirstOrDefault();
            if (names.Length == 0) StatusText.Text = "В Windows не найдены принтеры. Добавьте принтер и обновите список. Предпросмотр доступен без принтера.";
        }
        catch (PrintSystemException)
        { StatusText.Text = "Не удалось получить список принтеров. Проверьте службу печати Windows и обновите список."; }
    }

    private void ReloadPrintersClick(object sender, RoutedEventArgs e) => LoadPrinters();
    private void PrinterChanged(object sender, SelectionChangedEventArgs e)
    { if (PrinterBox.SelectedItem is string name) { settings.PrinterName = name; SaveSettings(); } }

    private void TemplateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TemplateBox.SelectedItem is not LabelTemplate template) return;
        settings.TemplateId = template.Id;
        TemplateDescription.Text = template.Description;
        UpdateTemplatePreview();
        SaveSettings();
    }

    private void WeightPriceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WeightPriceBox.SelectedValue is not WeightPriceMode mode) return;
        settings.WeightPriceMode = mode; UpdateTemplatePreview(); SaveSettings();
    }

    private void UpdateTemplatePreview() => TemplatePreview.Child = LabelDocument.CreateLabel(
        new Product("sample", "Салат Оливье", 850m), false, LabelTemplates.Find(settings.TemplateId), settings.WeightPriceMode);

    private FixedDocument BuildDocument()
    {
        var orders = rows.Where(r => r.IsSelected).Select(r =>
        {
            if (!r.TryQuantity(out var quantity)) throw new ArgumentException("Исправьте количество у товара «" + r.Name + "».");
            return new LabelOrder(r.Product, quantity);
        });
        return LabelDocument.Create(LabelLayout.Create(orders), demo, LabelTemplates.Find(settings.TemplateId), settings.WeightPriceMode);
    }

    private void PreviewClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var doc = BuildDocument();
            var window = new PreviewWindow(doc, () => SendToPrinter(doc)) { Owner = this };
            window.ShowDialog();
        }
        catch (ArgumentException ex) { ShowError(ex.Message); }
    }

    private void PrintClick(object sender, RoutedEventArgs e)
    {
        try { SendToPrinter(BuildDocument()); }
        catch (ArgumentException ex) { ShowError(ex.Message); }
    }

    private void SendToPrinter(FixedDocument doc)
    {
        if (PrinterBox.SelectedItem is not string printer)
        { ShowError("Выберите установленный принтер Windows."); return; }
        var note = demo ? "\nЭто демонстрационные товары, не данные R-Keeper." : $"\nЦены загружены {loadedAt:dd.MM.yyyy HH:mm}.";
        if (MessageBox.Show(this, $"Принтер: {printer}\nЛистов А4: {doc.Pages.Count}. Масштаб 100%." + note +
            "\nОтправить задание на печать?", "Печать ценников", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            LabelDocument.Print(doc, printer);
            StatusText.Text = $"Задание отправлено в очередь «{printer}». Листов: {doc.Pages.Count}. Проверьте результат на принтере.";
        }
        catch (Exception ex) when (ex is PrintSystemException or InvalidOperationException or ArgumentException)
        { ShowError("Печать не выполнена. " + ex.Message); }
    }

    private bool SaveSettings()
    {
        if (!settingsWritable) return false;
        try { SettingsStore.Save(settings); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { ShowError("Не удалось сохранить настройки в " + SettingsStore.FilePath + ". Проверьте права доступа."); return false; }
    }
    private void ShowError(string message) => MessageBox.Show(this, message, "Кушать · Ценники", MessageBoxButton.OK, MessageBoxImage.Warning);
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (loading is not null)
        { loading.Cancel(); e.Cancel = true; StatusText.Text = "Отмена загрузки… После завершения закройте окно ещё раз."; return; }
        credential = ""; http.Dispose();
    }
}
