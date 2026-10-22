using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Globalization;
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
    private MenuBrowser menuBrowser = new([], []);
    private double menuWidth = 190;
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
    private ConnectionReport? serverReport;
    private CancellationTokenSource? loading;

    private bool initializing = true;
    private LabelGeometry? labelGeometry;
    public MainWindow() : this(null, true) { }
    internal MainWindow(AppSettings? initialSettings, bool loadPrinters)
    {
        InitializeComponent();
        Title = $"Кушать · Печать ценников · {App.DisplayVersion}";
        Width = Math.Min(1320, SystemParameters.WorkArea.Width - 24);
        view = CollectionViewSource.GetDefaultView(rows);
        view.Filter = item => item is ProductRow row && menuBrowser.Matches(row.Product, SearchBox.Text);
        ProductsGrid.ItemsSource = view;
        try { settings = initialSettings ?? SettingsStore.Load(); settings.Normalize(); settingsWritable = initialSettings is null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or FormatException)
        { ShowError("Не удалось прочитать настройки. Их можно задать заново. Файл: " + SettingsStore.FilePath); }
        TemplateBox.ItemsSource = LabelTemplates.All;
        TemplateBox.SelectedValue = settings.TemplateId;
        LabelWidthBox.Text = settings.LabelWidthMm!.Value.ToString("0.##", CultureInfo.CurrentCulture);
        LabelHeightBox.Text = settings.LabelHeightMm!.Value.ToString("0.##", CultureInfo.CurrentCulture);
        PresetBox.ItemsSource = settings.LabelPresets.ToArray(); PresetBox.SelectedValue = settings.ActiveLabelPresetId;
        PresetNameBox.Text = settings.LabelPresets.FirstOrDefault(p => p.Id == settings.ActiveLabelPresetId)?.Name ?? "";
        ThemeBox.ItemsSource = ThemeManager.All; ThemeBox.SelectedValue = settings.ThemeId;
        ThemeManager.Apply(this, settings.ThemeId);
        initializing = false;
        ApplyDimensions();
        if (loadPrinters) LoadPrinters(); UpdateSummary();
        MenuTree.ItemsSource = new[] { menuBrowser.Root }; UpdateMenuFilter();
        if (initialSettings is null) Loaded += AutoConnectLoaded;
        UpdateResponsiveLayout();
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) => UpdateMenuFilter();

    private void MenuBranchChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is not MenuBranch branch) return;
        menuBrowser.Select(branch); UpdateMenuFilter();
    }

    private void UpdateMenuFilter()
    {
        if (view is null) return;
        view.Refresh();
        MenuFilterText.Text = $"{menuBrowser.SelectedBranch.Path} · Показано: {view.Cast<ProductRow>().Count()} из {rows.Count}";
    }

    private void ToggleMenuClick(object sender, RoutedEventArgs e)
    {
        var hide = MenuPanel.Visibility == Visibility.Visible;
        if (hide && MenuColumn.ActualWidth > 0) menuWidth = MenuColumn.ActualWidth;
        MenuColumn.Width = new GridLength(hide ? 0 : menuWidth);
        MenuPanel.Visibility = MenuSplitter.Visibility = hide ? Visibility.Collapsed : Visibility.Visible;
        MenuToggleButton.Content = hide ? "Показать ветки" : "Скрыть ветки";
    }

    private void ReplaceProducts(IEnumerable<Product> products, bool isDemo, DateTime? sourceTime = null, IReadOnlyList<MenuGroup>? groups = null)
    {
        var materialized = products.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        menuBrowser = new MenuBrowser(groups ?? catalog?.MenuGroups ?? [], materialized);
        foreach (var row in rows) row.PropertyChanged -= RowChanged;
        rows.Clear();
        foreach (var product in materialized)
        {
            var row = new ProductRow(product);
            row.PropertyChanged += RowChanged; rows.Add(row);
        }
        MenuTree.ItemsSource = new[] { menuBrowser.Root };
        MenuNoticeText.Text = catalog?.GroupWarning ?? (menuBrowser.HasIncompleteHierarchy
            ? "Часть групп показана на верхнем уровне: структура меню неполная."
            : materialized.Length == 0 ? "Загрузите меню из R-Keeper."
            : "Выберите ветку. Поиск работает внутри неё и её подветок.");
        UpdateMenuFilter();
        demo = isDemo; loadedAt = sourceTime ?? DateTime.Now; UpdateSummary(); UpdateTemplatePreview();
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
            var result = await new ReferenceConnectionWorkflow(http).ConnectAsync(settings.Connection, credential,
                report => { serverReport = report; StatusText.Text = report.Summary + " Загрузка меню…"; }, loading.Token);
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
        RefreshButton.IsEnabled = ProductsGrid.IsEnabled = ConnectButton.IsEnabled = MenuTree.IsEnabled =
            PriceTypeBox.IsEnabled = SelectButton.IsEnabled = ClearButton.IsEnabled = !busy;
        PrinterSettings.IsEnabled = LabelSettings.IsEnabled = !busy;
        PreviewButton.IsEnabled = PrintButton.IsEnabled = !busy && labelGeometry is not null;
        LogoutButton.IsEnabled = !busy && !string.IsNullOrEmpty(credential);
        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CancelClick(object sender, RoutedEventArgs e) => loading?.Cancel();

    private void RowChanged(object? sender, PropertyChangedEventArgs e) { UpdateSummary(); UpdateTemplatePreview(); }

    private void UpdateSummary()
    {
        var selected = rows.Where(r => r.IsSelected).ToArray();
        if (selected.Any(r => !r.TryQuantity(out _)))
        { SummaryText.Text = "Исправьте количество у выбранных товаров: целое число от 1 до 999."; return; }
        var count = selected.Sum(r => { r.TryQuantity(out var n); return n; });
        SummaryText.Text = $"Выбрано товаров: {selected.Length}\nЦенников: {count}\nЛистов А4: {(count + (labelGeometry?.Capacity ?? 4) - 1) / (labelGeometry?.Capacity ?? 4)}" +
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
        serverReport = dialog.ServerReport;
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

    internal void AcceptCatalog(ReferenceCatalog result, string? selectedType)
    {
        catalog = result; catalogLoadedAt = DateTime.Now; priceTypeId = null;
        ConnectionText.Text = $"Сервер: {settings.Connection.ServerAddress} · работник: {settings.Connection.UserName}";
        if (serverReport?.Certificate is { } cert) ConnectionText.Text += $" · Сертификат до {cert.ValidUntilUtc:dd.MM.yyyy}";
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
            StatusText.Text = $"Загружено товаров: {rows.Count}. Без цены и недоступно для печати: {priced.WithoutPrice}. Обновлено: {loadedAt:dd.MM.yyyy HH:mm}. На ценнике — одна цена: за 100 г или за штуку. Не определена единица: {rows.Count(r => !r.CanPrint)}; эти товары недоступны для печати.";
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
        settings.TemplateId = template.Id; TemplateDescription.Text = template.Description;
        if (initializing) return;
        ClearActivePreset(); UpdateTemplatePreview(); SaveSettings();
    }
    private void DimensionsChanged(object sender, TextChangedEventArgs e)
    { if (!initializing) { ClearActivePreset(); ApplyDimensions(); } }
    private void ClearActivePreset()
    {
        settings.ActiveLabelPresetId = null;
        initializing = true; PresetBox.SelectedIndex = -1; initializing = false;
    }
    private void ApplyDimensions()
    {
        try
        {
            if (!double.TryParse(LabelWidthBox.Text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var width) ||
                !double.TryParse(LabelHeightBox.Text.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var height))
                throw new ArgumentException("Введите ширину и высоту в миллиметрах.");
            labelGeometry = LabelLayout.Geometry(width, height);
            settings.LabelWidthMm = width; settings.LabelHeightMm = height;
            settings.LabelOrientation = width > height ? LabelOrientation.Landscape : LabelOrientation.Portrait;
            FormatErrorText.Text = ""; SaveSettings();
        }
        catch (ArgumentException ex) { labelGeometry = null; FormatErrorText.Text = ex.Message; }
        PreviewButton.IsEnabled = PrintButton.IsEnabled = labelGeometry is not null && loading is null;
        UpdateTemplatePreview(); UpdateSummary();
    }
    private void SwapDimensionsClick(object sender, RoutedEventArgs e)
    {
        initializing = true;
        (LabelWidthBox.Text, LabelHeightBox.Text) = (LabelHeightBox.Text, LabelWidthBox.Text);
        initializing = false; ClearActivePreset(); ApplyDimensions();
    }
    private void SavePresetClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var geometry = labelGeometry ?? throw new ArgumentException("Сначала исправьте размер ценника.");
            var preset = settings.SaveLabelPreset(PresetNameBox.Text, settings.TemplateId, geometry.LabelWidthMm, geometry.LabelHeightMm);
            initializing = true; PresetBox.ItemsSource = settings.LabelPresets.ToArray(); PresetBox.SelectedValue = preset.Id; initializing = false;
            PresetStatusText.Text = SaveSettings() ? "Шаблон сохранён. Он доступен при следующем запуске." : "Шаблон выбран для текущего сеанса.";
        }
        catch (ArgumentException ex) { PresetStatusText.Text = ex.Message; }
    }
    private void PresetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || PresetBox.SelectedItem is not LabelPreset preset) return;
        initializing = true;
        settings.ActiveLabelPresetId = preset.Id; settings.TemplateId = preset.TemplateId;
        TemplateBox.SelectedValue = preset.TemplateId; PresetNameBox.Text = preset.Name;
        LabelWidthBox.Text = preset.WidthMm.ToString("0.##", CultureInfo.CurrentCulture);
        LabelHeightBox.Text = preset.HeightMm.ToString("0.##", CultureInfo.CurrentCulture);
        initializing = false; ApplyDimensions();
    }
    private void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (initializing || ThemeBox.SelectedItem is not UiTheme theme) return;
        settings.ThemeId = theme.Id; ThemeManager.Apply(this, theme.Id); SaveSettings();
    }
    private void ProductFocusChanged(object sender, SelectionChangedEventArgs e) => UpdateTemplatePreview();
    private void UpdateTemplatePreview()
    {
        if (initializing || TemplatePreview is null) return;
        if (labelGeometry is null) { TemplatePreview.Child = null; PreviewSizeText.Text = "Укажите допустимый размер ценника."; return; }
        var product = (ProductsGrid.SelectedItem as ProductRow)?.Product;
        if (product?.CanPrint != true) product = rows.FirstOrDefault(r => r.IsSelected && r.CanPrint)?.Product ?? view?.Cast<ProductRow>().FirstOrDefault(r => r.CanPrint)?.Product;
        product ??= new Product("sample", "Название товара", 145m, SaleUnit.Piece);
        try
        {
            TemplatePreview.Child = LabelDocument.CreateLabel(product, false, LabelTemplates.Find(settings.TemplateId), layout: labelGeometry);
            PreviewSizeText.Text = $"{labelGeometry.LabelWidthMm:0.##} × {labelGeometry.LabelHeightMm:0.##} мм · {labelGeometry.Capacity} на А4 · 100%";
        }
        catch (ArgumentException ex) { TemplatePreview.Child = null; PreviewSizeText.Text = ex.Message; }
    }
    private void WindowResized(object sender, SizeChangedEventArgs e) => UpdateResponsiveLayout();
    private void UpdateResponsiveLayout()
    {
        if (BodyGrid is null) return;
        var stacked = ActualWidth > 0 && ActualWidth < 1080;
        PrintColumn.Width = new GridLength(stacked ? 0 : 360);
        Grid.SetColumn(PrintCard, stacked ? 0 : 1); Grid.SetRow(PrintCard, stacked ? 1 : 0);
        CatalogPanel.Margin = new Thickness(0, 0, stacked ? 0 : 16, stacked ? 16 : 0);
        CatalogPanel.Height = Math.Max(330, ActualHeight - 300);
        PrintCard.Height = stacked ? double.NaN : CatalogPanel.Height;
        MenuColumn.MaxWidth = stacked ? 180 : 300;
    }

    private FixedDocument BuildDocument()
    {
        var orders = rows.Where(r => r.IsSelected).Select(r =>
        {
            if (!r.TryQuantity(out var quantity)) throw new ArgumentException("Исправьте количество у товара «" + r.Name + "».");
            return new LabelOrder(r.Product, quantity);
        });
        return LabelDocument.Create(LabelLayout.Create(orders, labelGeometry ?? throw new ArgumentException("Исправьте размер ценника.")), demo, LabelTemplates.Find(settings.TemplateId), layout: labelGeometry);
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
