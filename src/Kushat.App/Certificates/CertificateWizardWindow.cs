using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Kushat.CertificateTool;
using Kushat.Core;
using Microsoft.Win32;

namespace Kushat.App;

public sealed class CertificateWizardWindow : Window
{
    private readonly TextBox address = new() { MinHeight = 34 };
    private readonly ComboBox servers = new() { DisplayMemberPath = nameof(LocalReferenceServer.DisplayName), MinHeight = 34 };
    private readonly ComboBox folders = new() { MinHeight = 34 };
    private readonly TextBlock heading = new() { FontSize = 24, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock guidance = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 18) };
    private readonly TextBox results = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 180,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock serverState = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12), FontWeight = FontWeights.SemiBold };
    private readonly CheckBox renew = new() { Content = "Выпустить новый сертификат", Margin = new Thickness(0, 8, 0, 12) };
    private readonly Button scan = new() { Content = "Найти и проверить" };
    private readonly Button stop = new() { Content = "Остановить службу" };
    private readonly Button start = new() { Content = "Запустить службу" };
    private readonly Button next = new() { Content = "Далее →" };
    private readonly Button finish = new() { Content = "Завершить", Visibility = Visibility.Collapsed };
    private readonly Button rollback = new() { Content = "Откатить сертификат", Visibility = Visibility.Collapsed };
    private readonly Button import = new() { Content = "Доверие из публичного .cer…" };
    private readonly Button browseKeys = new() { Content = "Папка httpKeys…" };
    private string? manualKeys;
    private string? manualKeysServer;
    private readonly Button browse = new() { Content = "Папка RefSrv…" };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly CertificateWizardProgress progress = new();
    private IReadOnlyList<CertificateFile> files = [];
    private ConnectionReport? report;
    private Uri? endpoint;
    private LocalReferenceServer? selected;
    private string? prepared;
    private string originalFingerprint = "";
    private byte[]? newPublic;
    private bool trustAdded;
    private bool installed;
    private bool busy;
    private bool polling;
    private bool bound;
    private bool observedStopped;
    private bool initializing;
    private ReferenceServiceState? inspectedServiceState;

    public CertificateWizardWindow(string initialAddress = "")
    {
        Title = $"Кушать · Настройка сертификата · {App.DisplayVersion}";
        Width = 880; Height = 790; MinWidth = 650; MinHeight = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(heading); panel.Children.Add(guidance);
        AddField(panel, "Адрес Reference-сервера с HTTPS-портом", address);
        AddField(panel, "Reference-сервер на этом компьютере", servers);
        AddField(panel, "Каталог сертификатов httpKeys", folders);
        var inspectionButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var b in new[] { scan, browse, browseKeys, import }) inspectionButtons.Children.Add(b);
        panel.Children.Add(inspectionButtons); panel.Children.Add(renew); panel.Children.Add(results); panel.Children.Add(serverState);
        var actions = new WrapPanel();
        foreach (var b in new[] { stop, start, rollback, next, finish }) actions.Children.Add(b);
        panel.Children.Add(actions);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        address.Text = initialAddress;
        address.TextChanged += (_, _) => { if (!initializing && progress.Step == CertificateWizardStep.Inspect) InvalidateInspection(); };
        servers.SelectionChanged += (_, _) => { if (!initializing && progress.Step == CertificateWizardStep.Inspect) InvalidateInspection(); };
        folders.SelectionChanged += (_, _) => RefreshButtons();
        renew.Checked += (_, _) => RefreshButtons(); renew.Unchecked += (_, _) => RefreshButtons();
        scan.Click += async (_, _) => await RunAsync(AnalyzeAsync);
        browse.Click += async (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Папка с RefSrv.exe" };
            if (dialog.ShowDialog(this) != true) return;
            var exe = Path.Combine(dialog.FolderName, "RefSrv.exe");
            if (!File.Exists(exe)) { MessageBox.Show(this, "В папке не найден RefSrv.exe."); return; }
            initializing = true;
            var list = (servers.ItemsSource as IEnumerable<LocalReferenceServer> ?? []).ToList();
            var item = list.FirstOrDefault(s => s.ExePath.Equals(exe, StringComparison.OrdinalIgnoreCase)) ?? new LocalReferenceServer(exe, null, 0, []);
            if (!list.Contains(item)) list.Add(item);
            servers.ItemsSource = list; servers.SelectedItem = item; initializing = false;
            await RunAsync(AnalyzeAsync);
        };
        browseKeys.Click += async (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Каталог httpKeys выбранного Reference-сервера" };
            if (dialog.ShowDialog(this) != true) return;
            if (!File.Exists(Path.Combine(dialog.FolderName, "default.pem")))
            { MessageBox.Show(this, "В папке не найден default.pem."); return; }
            manualKeys = dialog.FolderName; manualKeysServer = (servers.SelectedItem as LocalReferenceServer)?.ExePath;
            await RunAsync(AnalyzeAsync);
        };
        import.Click += async (_, _) => await RunAsync(ImportClientAsync);
        stop.Click += async (_, _) => await RunAsync(async () =>
        {
            if (selected is null || endpoint is null) return;
            var fresh = await WindowsReferenceDiscovery.FindAsync(lifetime.Token);
            var current = fresh.SingleOrDefault(s => s.ServiceName == selected.ServiceName && s.ExePath.Equals(selected.ExePath, StringComparison.OrdinalIgnoreCase));
            if (current is null || (WindowsServiceControl.State(current.ServiceName!) != ReferenceServiceState.Stopped && !current.Ports.Contains(endpoint.Port)))
                throw new InvalidOperationException("Порт больше не принадлежит выбранной службе. Повторите проверку.");
            await Task.Run(() => WindowsServiceControl.StopAsync(current));
            serverState.Text = "Запрошена штатная остановка. Проверяем службу, процесс и порт…";
        });
        start.Click += async (_, _) => await RunAsync(async () =>
        {
            if (selected is not null) await Task.Run(() => WindowsServiceControl.StartAsync(selected));
            serverState.Text = "Запрошен запуск. Ожидаем службу, процесс и HTTPS…";
            if (progress.Step == CertificateWizardStep.Inspect) timer.Start();
        });
        next.Click += async (_, _) => await RunAsync(AdvanceAsync);
        finish.Click += (_, _) => Close();
        rollback.Click += async (_, _) => await RunAsync(RollbackAsync);
        timer.Tick += async (_, _) => await PollAsync();
        Loaded += async (_, _) => await RunAsync(AnalyzeAsync);
        Closing += OnClosing;
        SetStepText(); RefreshButtons();
    }

    private void InvalidateInspection()
    {
        timer.Stop(); selected = null; inspectedServiceState = null;
        report = null; bound = false; files = []; folders.ItemsSource = null;
        serverState.Text = "";
        results.Text = "Нажмите «Найти и проверить»."; RefreshButtons();
    }

    private async Task AnalyzeAsync()
    {
        report = null; bound = false; files = [];
        results.Text = "Поиск службы Reference, портов и сертификатов…";
        var found = await WindowsReferenceDiscovery.FindAsync(lifetime.Token);
        initializing = true;
        var previous = servers.SelectedItem as LocalReferenceServer;
        var list = found.ToList();
        if (previous is not null && !list.Any(s => s.ExePath.Equals(previous.ExePath, StringComparison.OrdinalIgnoreCase))) list.Add(previous);
        servers.ItemsSource = list;
        if (previous is not null) servers.SelectedItem = list.FirstOrDefault(s => s.ExePath == previous.ExePath && s.ServiceName == previous.ServiceName);
        if (servers.SelectedItem is null && list.Count == 1) servers.SelectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(address.Text))
        {
            endpoint = new ReferenceConnection { ServerAddress = address.Text }.GetServerEndpoint();
            var byPort = list.Where(s => s.Ports.Contains(endpoint.Port)).ToArray();
            if (byPort.Length == 1 && await WindowsReferenceDiscovery.IsLocalHostAsync(endpoint.IdnHost, lifetime.Token)) servers.SelectedItem = byPort[0];
        }
        selected = servers.SelectedItem as LocalReferenceServer;
        if (!string.Equals(manualKeysServer, selected?.ExePath, StringComparison.OrdinalIgnoreCase)) manualKeys = null;
        inspectedServiceState = selected?.ServiceName is { } serviceName ? WindowsServiceControl.State(serviceName) : null;
        if (selected is not null) serverState.Text = $"Служба: {(inspectedServiceState is { } state ? StateTitle(state) : "не найдена")}; процесс: {(WindowsReferenceDiscovery.IsProcessRunning(selected.ExePath) ? "работает" : "остановлен")}.";
        if (string.IsNullOrWhiteSpace(address.Text) && selected is not null)
        {
            foreach (var port in selected.Ports.Take(8))
            {
                var candidate = new Uri($"https://127.0.0.1:{port}");
                var check = await new ConnectionPreflight().CheckAsync(candidate, 2, lifetime.Token);
                if (check.Certificate is not null) { address.Text = candidate.GetLeftPart(UriPartial.Authority); break; }
            }
        }
        initializing = false;
        if (string.IsNullOrWhiteSpace(address.Text))
        {
            results.Text = "Укажите адрес и HTTPS-порт. Найденные порты выбранного RefSrv: " +
                (selected is null ? "сервер не найден. Запустите мастер на компьютере Reference-сервера." : string.Join(", ", selected.Ports));
            return;
        }
        endpoint = new ReferenceConnection { ServerAddress = address.Text }.GetServerEndpoint();
        report = await new ConnectionPreflight().CheckAsync(endpoint, 8, lifetime.Token);
        var local = await WindowsReferenceDiscovery.IsLocalHostAsync(endpoint.IdnHost, lifetime.Token);
        bound = local && selected is not null && (selected.Ports.Contains(endpoint.Port) ||
            (!report.PortOpen && !WindowsReferenceDiscovery.IsProcessRunning(selected.ExePath)));
        files = selected is null ? [] : await Task.Run(() => CertificateDiscovery.Find(selected.Directory, endpoint.IdnHost));
        if (manualKeys is not null)
            files = files.Concat(await Task.Run(() => CertificateDiscovery.Find(manualKeys, endpoint.IdnHost))).DistinctBy(f => f.Path).ToArray();
        var directories = files.Select(f => Path.GetDirectoryName(f.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var matched = report.Certificate is null ? null : CertificateDiscovery.MatchKeysDirectory(files, report.Certificate.Sha256);
        folders.ItemsSource = directories;
        folders.SelectedItem = matched ?? (manualKeys is not null && directories.Contains(manualKeys) ? manualKeys : directories.Length == 1 ? directories[0] : null);
        var currentFile = files.Where(f => Path.GetDirectoryName(f.Path) == (folders.SelectedItem as string))
            .OrderBy(f => Path.GetFileName(f.Path).Equals("mylocal.pem", StringComparison.OrdinalIgnoreCase) ? 0 : 1).FirstOrDefault();
        renew.IsChecked = report.Certificate is null && !report.PortOpen && currentFile is not null
            ? !currentFile.IsCurrent(DateTime.UtcNow) || !currentFile.MatchesHost : NeedsNewCertificate(report, endpoint);
        results.Text = report.Text + "\n\nНайденные файлы:\n" + string.Join("\n", files.Select(f =>
            $"{f.Path}{(f.Sha256 == report.Certificate?.Sha256 ? " [предъявляется сервером]" : "")}\n  до {f.ValidUntilUtc:u}; {(f.IsCurrent(DateTime.UtcNow) ? "срок действителен" : "срок недействителен")}; имя {(f.MatchesHost ? "совпадает" : "не совпадает")}"));
        if (!local) results.Text += "\n\nЭто другой компьютер. Для замены запустите мастер на Reference-сервере. Здесь можно добавить доверие из его публичного .cer.";
        if (bound && report.Certificate is not null && matched is null)
            results.Text += "\n\nСертификат порта не сопоставлен однозначно с файлом. Выберите правильный каталог httpKeys или проверьте путь базы сервера.";
        if (endpoint.Scheme != "https") results.Text += "\n\nДля настройки сертификата укажите HTTPS-порт.";
        RefreshButtons();
    }

    private async Task AdvanceAsync()
    {
        if (progress.Step == CertificateWizardStep.Inspect)
        {
            if (report is null || endpoint is null) return;
            if (renew.IsChecked != true)
            {
                if (!report.Ready)
                {
                    if (!CanTrustExisting()) throw new InvalidOperationException("Сначала исправьте соединение или добавьте доверие из проверенного .cer.");
                    if (!CertificateToolsLauncher.IsAdministrator) { CertificateToolsLauncher.Open(address.Text); _ = Dispatcher.BeginInvoke(new Action(Close)); return; }
                    var peer = report.Certificate!;
                    var added = CertificateTrust.Install(peer.Der, endpoint, peer.Sha256, StoreLocation.LocalMachine);
                    var verified = await new ConnectionPreflight().CheckAsync(endpoint, 8, lifetime.Token);
                    if (!verified.Ready || verified.Certificate?.Sha256 != peer.Sha256)
                    {
                        if (added) CertificateTrust.RemoveAdded(peer.Der, StoreLocation.LocalMachine);
                        throw new ConnectionCheckException(verified);
                    }
                    report = verified; results.Text = report.Text + "\nДоверие настроено. Замена файла и перезапуск не требуются.";
                }
                progress.CompleteExisting(report); SetStepText(); return;
            }
            if (!CanPrepare()) throw new InvalidOperationException("Не подтверждены локальный сервер и его каталог сертификатов. Повторите поиск.");
            if (!CertificateToolsLauncher.IsAdministrator) { CertificateToolsLauncher.Open(address.Text); _ = Dispatcher.BeginInvoke(new Action(Close)); return; }
            var keys = (string)folders.SelectedItem;
            originalFingerprint = report.Certificate?.Sha256 ?? files.Where(f => Path.GetDirectoryName(f.Path) == keys)
                .OrderBy(f => Path.GetFileName(f.Path).Equals("mylocal.pem", StringComparison.OrdinalIgnoreCase) ? 0 : 1).First().Sha256;
            prepared = await Task.Run(() => ReferenceCertificateMaintenance.Prepare(selected!.Directory, keys, endpoint.IdnHost));
            newPublic = await File.ReadAllBytesAsync(Path.Combine(prepared, "rkeeper-local.cer"), lifetime.Token);
            using var cert = X509CertificateLoader.LoadCertificate(newPublic);
            progress.Prepared(cert.GetCertHashString(HashAlgorithmName.SHA256));
            results.Text = $"Подготовка: {prepared}\nНовый сертификат до {cert.NotAfter.ToUniversalTime():u}\nSHA256: {progress.ExpectedFingerprint}\n\nРабочие файлы пока не изменены. Резервная копия сохранена.";
            SetStepText(); timer.Start();
        }
        else if (progress.Step == CertificateWizardStep.StopServer)
        {
            await ObserveServerAsync();
            if (!progress.CanContinue || prepared is null || endpoint is null || newPublic is null) throw new InvalidOperationException("Сервер ещё не остановлен полностью.");
            var wrote = false;
            try
            {
                await Task.Run(() => ReferenceCertificateMaintenance.Apply("install", prepared)); wrote = true;
                trustAdded = CertificateTrust.Install(newPublic, endpoint, progress.ExpectedFingerprint, StoreLocation.LocalMachine);
                installed = true; progress.Installed();
            }
            catch
            {
                if (wrote) await Task.Run(() => ReferenceCertificateMaintenance.Apply("rollback", prepared));
                throw;
            }
            results.Text += "\n\nmylocal.pem установлен. Доверие настроено для пользователей этого компьютера.\nСервер можно включить.";
            SetStepText();
        }
        else if (progress.Step == CertificateWizardStep.StartServer)
        {
            await ObserveServerAsync(); progress.Complete(); timer.Stop(); SetStepText();
        }
    }

    private bool CanPrepare()
    {
        if (!bound || selected is null || folders.SelectedItem is not string keys || endpoint?.Scheme != "https" || report is null) return false;
        if (report.Certificate is null) return !report.PortOpen;
        return files.Any(f => Path.GetDirectoryName(f.Path) == keys && f.Sha256 == report.Certificate.Sha256);
    }

    private bool CanTrustExisting() => CanPrepare() && report?.Certificate is { } peer && endpoint is not null &&
        !NeedsNewCertificate(report, endpoint);

    private static bool NeedsNewCertificate(ConnectionReport check, Uri target)
    {
        if (target.Scheme != "https") return false;
        if (check.Certificate is not { } peer) return true;
        using var cert = X509CertificateLoader.LoadCertificate(peer.Der);
        return cert.NotBefore.ToUniversalTime() > DateTime.UtcNow || cert.NotAfter.ToUniversalTime() < DateTime.UtcNow.AddDays(30) ||
            !cert.MatchesHostname(target.IdnHost, false, false);
    }

    private async Task ObserveServerAsync()
    {
        if (selected is null) return;
        if (progress.Step == CertificateWizardStep.Inspect)
        {
            inspectedServiceState = selected.ServiceName is { } name ? WindowsServiceControl.State(name) : null;
            serverState.Text = $"Служба: {(inspectedServiceState is { } state ? StateTitle(state) : "не найдена")}.";
            if (inspectedServiceState == ReferenceServiceState.Running && WindowsReferenceDiscovery.IsProcessRunning(selected.ExePath))
            {
                await AnalyzeAsync();
                if (report?.Certificate is not null || report?.Ready == true) timer.Stop();
            }
            return;
        }
        if (endpoint is null) return;
        var running = WindowsReferenceDiscovery.IsProcessRunning(selected.ExePath);
        var service = selected.ServiceName is null ? (running ? ReferenceServiceState.Running : ReferenceServiceState.Stopped)
            : WindowsServiceControl.State(selected.ServiceName);
        var portOpen = progress.Step == CertificateWizardStep.StopServer
            ? System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(p => p.Port == endpoint.Port)
            : await PortOpenAsync(endpoint);
        observedStopped = service == ReferenceServiceState.Stopped && !running && !portOpen;
        if (progress.Step == CertificateWizardStep.StopServer)
        {
            progress.ObserveStopped(service == ReferenceServiceState.Stopped, running, portOpen);
            serverState.Text = observedStopped ? "Сервер отключён: служба остановлена, процесса нет, порт закрыт. Можно нажать «Далее»."
                : $"Служба: {StateTitle(service)}; процесс: {(running ? "работает" : "остановлен")}; порт: {(portOpen ? "открыт" : "закрыт")}. Ожидаем полную остановку…";
        }
        else if (progress.Step == CertificateWizardStep.StartServer)
        {
            if (service == ReferenceServiceState.Running && running && portOpen)
            {
                report = await new ConnectionPreflight().CheckAsync(endpoint, 3, lifetime.Token);
                progress.ObserveStarted(true, true, report);
                serverState.Text = progress.CanContinue ? (progress.RolledBack ? "Служба запущена. Исходный сертификат восстановлен." : "Служба запущена. Порт открыт, новый сертификат действителен и доверен. Можно нажать «Далее».") : report.Text;
                if (report.Certificate?.Sha256 != progress.ExpectedFingerprint) serverState.Text += "\nСервер ещё не предъявил ожидаемый сертификат.";
            }
            else
            {
                progress.ObserveStarted(false, running, new(endpoint, portOpen, false, "Ожидание запуска"));
                serverState.Text = $"Сервер можно включить. Служба: {StateTitle(service)}; процесс: {(running ? "работает" : "остановлен")}; порт: {(portOpen ? "открыт" : "закрыт")}.";
            }
        }
    }

    private async Task PollAsync()
    {
        if (busy || polling || lifetime.IsCancellationRequested) return;
        polling = true; RefreshButtons();
        try { await ObserveServerAsync(); }
        catch (Exception ex) when (IsExpected(ex))
        {
            observedStopped = false;
            if (progress.Step == CertificateWizardStep.StopServer) progress.ObserveStopped(false, true, true);
            else if (progress.Step == CertificateWizardStep.StartServer && endpoint is not null) progress.ObserveStarted(false, false, new(endpoint, false, false, "Проверка не выполнена"));
            serverState.Text = "Не удалось проверить состояние: " + SafeMessage(ex);
        }
        finally { polling = false; RefreshButtons(); }
    }

    private async Task ImportClientAsync()
    {
        endpoint = new ReferenceConnection { ServerAddress = address.Text }.GetServerEndpoint();
        if (endpoint.Scheme != "https") throw new ArgumentException("Для проверки сертификата укажите HTTPS.");
        var dialog = new OpenFileDialog { Title = "Публичный .cer, полученный с Reference-сервера", Filter = "Сертификат (*.cer)|*.cer" };
        if (dialog.ShowDialog(this) != true) return;
        var bytes = await File.ReadAllBytesAsync(dialog.FileName, lifetime.Token);
        report = await new ConnectionPreflight().CheckAsync(endpoint, 8, lifetime.Token);
        var fingerprint = report.Certificate?.Sha256 ?? throw new InvalidOperationException("Порт не предъявил HTTPS-сертификат.");
        CertificateTrust.ValidatePublic(bytes, endpoint, fingerprint);
        var location = CertificateToolsLauncher.IsAdministrator ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;
        var added = CertificateTrust.Install(bytes, endpoint, fingerprint, location);
        report = await new ConnectionPreflight().CheckAsync(endpoint, 8, lifetime.Token);
        if (!report.Ready || report.Certificate?.Sha256 != fingerprint)
        {
            if (added) CertificateTrust.RemoveAdded(bytes, location);
            throw new ConnectionCheckException(report);
        }
        results.Text = report.Text + "\nДоверие добавлено. Новая генерация и перезапуск сервера не потребовались.";
        renew.IsChecked = false; RefreshButtons();
    }

    private async Task RollbackAsync()
    {
        if (selected is null || endpoint is null || prepared is null || !installed) return;
        await ObserveServerAsync();
        if (!observedStopped) throw new InvalidOperationException("Для отката сначала штатно остановите службу. Мастер проверит остановку.");
        if (MessageBox.Show(this, "Восстановить прежний mylocal.pem? Вернётся прежнее состояние сертификата.", "Откат", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        await Task.Run(() => ReferenceCertificateMaintenance.Apply("rollback", prepared));
        if (trustAdded && newPublic is not null) CertificateTrust.RemoveAdded(newPublic, StoreLocation.LocalMachine);
        trustAdded = false; installed = false; progress.ExpectRollback(originalFingerprint);
        results.Text += "\nОткат файлов выполнен. Можно запустить службу. Прежняя ошибка сертификата может вернуться.";
        SetStepText();
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || polling) return;
        busy = true; RefreshButtons();
        try { await action(); }
        catch (OperationCanceledException) { results.Text += "\nОперация отменена или истёк таймаут."; }
        catch (Exception ex) when (IsExpected(ex)) { results.Text += "\n\nОШИБКА: " + SafeMessage(ex); }
        finally { initializing = false; busy = false; RefreshButtons(); }
    }

    private static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or
        InvalidOperationException or CryptographicException or Win32Exception or System.Text.Json.JsonException;
    private static string SafeMessage(Exception ex) => ex is ConnectionCheckException check ? check.Report.Text :
        ex is Win32Exception win ? "Ошибка Windows: " + win.NativeErrorCode + ". Проверьте права администратора и состояние службы." : ex.Message;

    private async Task<bool> PortOpenAsync(Uri target)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(700));
        try { await client.ConnectAsync(target.IdnHost, target.Port, timeout.Token); return true; }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException) { return false; }
    }

    private static string StateTitle(ReferenceServiceState state) => state switch
    {
        ReferenceServiceState.Stopped => "остановлена", ReferenceServiceState.Running => "запущена",
        ReferenceServiceState.StartPending => "запускается", ReferenceServiceState.StopPending => "останавливается",
        ReferenceServiceState.Paused => "приостановлена", _ => "переходное состояние"
    };

    private void SetStepText()
    {
        heading.Text = progress.Step switch { CertificateWizardStep.Inspect => "1. Поиск и проверка", CertificateWizardStep.StopServer => "2. Отключите Reference-сервер",
            CertificateWizardStep.StartServer => "3. Сервер можно включить", _ => "4. Работа завершена" };
        guidance.Text = progress.Step switch
        {
            CertificateWizardStep.Inspect => "Проверим порт, сертификат сервера и локальные файлы. Изменения начнутся после «Далее». Для нового сертификата будет создана резервная копия.",
            CertificateWizardStep.StopServer => "Остановите выбранную службу кнопкой ниже или через службы Windows, когда перерыв допустим. «Далее» появится после подтверждённой остановки службы, процесса и порта.",
            CertificateWizardStep.StartServer => progress.RolledBack ? "Прежний файл восстановлен. Запустите службу; мастер проверит исходный отпечаток сертификата." : "Сертификат и доверие настроены. Запустите службу кнопкой ниже или обычным способом. Мастер проверит запуск, порт и отпечаток сертификата.",
            _ => endpoint?.Scheme == "http" ? "Порт доступен. Для HTTP сертификат не используется." : progress.RolledBack ? "Откат завершён, служба запущена с прежним сертификатом. Исходная ошибка сертификата может сохраняться." : "Порт доступен, HTTPS-сертификат действителен и доверен. Можно завершить мастер и войти в R-Keeper."
        };
        if (progress.Step == CertificateWizardStep.Complete) timer.Stop();
    }

    private void RefreshButtons()
    {
        var inspect = progress.Step == CertificateWizardStep.Inspect;
        address.IsEnabled = servers.IsEnabled = folders.IsEnabled = renew.IsEnabled = scan.IsEnabled = browse.IsEnabled = browseKeys.IsEnabled = import.IsEnabled = inspect && !busy && !polling;
        stop.Visibility = inspect || progress.Step == CertificateWizardStep.Complete ? Visibility.Collapsed : Visibility.Visible;
        start.Visibility = progress.Step == CertificateWizardStep.Complete || (inspect && selected?.ServiceName is null) ? Visibility.Collapsed : Visibility.Visible;
        stop.IsEnabled = start.IsEnabled = !busy && !polling && selected?.ServiceName is not null;
        rollback.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        rollback.IsEnabled = !busy && !polling && observedStopped;
        finish.Visibility = progress.Step == CertificateWizardStep.Complete ? Visibility.Visible : Visibility.Collapsed;
        next.Visibility = progress.Step == CertificateWizardStep.Complete || (!inspect && !progress.CanContinue) ? Visibility.Collapsed : Visibility.Visible;
        next.IsEnabled = !busy && !polling && (inspect ? report is not null && (renew.IsChecked == true ? CanPrepare() : (report.Ready || CanTrustExisting())) : progress.CanContinue);
        if (progress.Step == CertificateWizardStep.StopServer || (inspect && inspectedServiceState != ReferenceServiceState.Stopped)) start.IsEnabled = false;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy || polling) { e.Cancel = true; return; }
        if (progress.Step is CertificateWizardStep.StopServer or CertificateWizardStep.StartServer &&
            MessageBox.Show(this, "Мастер ещё не завершён. Убедитесь, что нужная служба запущена. Закрыть мастер?", "Настройка сертификата", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
        { e.Cancel = true; return; }
        timer.Stop(); lifetime.Cancel(); lifetime.Dispose();
    }

    private static void AddField(Panel parent, string title, Control input)
    {
        parent.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 5) });
        input.Margin = new Thickness(0, 0, 0, 12); parent.Children.Add(input);
    }
}
