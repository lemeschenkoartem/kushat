using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
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
    private readonly ComboBox servers = new() { DisplayMemberPath = nameof(LocalReferenceServer.DisplayName), MinHeight = 34 };
    private readonly ComboBox folders = new() { MinHeight = 34 };
    private readonly TextBlock heading = new() { FontSize = 24, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock guidance = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 18) };
    private readonly TextBox results = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 220,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock serverState = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12), FontWeight = FontWeights.SemiBold };
    private readonly Button scan = new() { Content = "Повторить поиск" };
    private readonly Button issue = new() { Content = "Выпустить новый сертификат", Visibility = Visibility.Collapsed };
    private readonly Button stop = new() { Content = "Остановить службу" };
    private readonly Button start = new() { Content = "Запустить службу" };
    private readonly Button next = new() { Content = "Далее →" };
    private readonly Button back = new() { Content = "← Назад" };
    private readonly Button cancel = new() { Content = "Отмена" };
    private readonly Button finish = new() { Content = "Завершить", Visibility = Visibility.Collapsed };
    private readonly Button rollback = new() { Content = "Откатить сертификат", Visibility = Visibility.Collapsed };
    private readonly Button browseKeys = new() { Content = "Папка httpKeys…" };
    private readonly Button browse = new() { Content = "Папка RefSrv…" };
    private readonly Button elevate = new() { Content = "Запустить от администратора" };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(3) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly CertificateWizardProgress progress = new();
    private IReadOnlyList<CertificateFile> files = [];
    private CertificateFile? currentFile;
    private LocalReferenceServer? selected;
    private ConnectionReport? report;
    private ReferenceServiceState? serviceState;
    private string? manualKeys;
    private string? manualKeysServer;
    private string? prepared;
    private string originalFingerprint = "";
    private byte[]? newPublic;
    private bool trustAdded;
    private bool installed;
    private bool busy;
    private bool polling;
    private bool initializing;
    private bool observedStopped;
    private bool serviceTouched;
    private bool closeRequested;
    private bool allowClose;
    private bool critical;
    private CancellationTokenSource? operation;
    private CancellationToken Token => operation?.Token ?? lifetime.Token;
    private static readonly Uri TrustAddress = new("https://localhost");

    public CertificateWizardWindow()
    {
        Title = $"Кушать · Сертификат Reference-сервера · {App.DisplayVersion}";
        Width = 880; Height = 750; MinWidth = 650; MinHeight = 550;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(heading); panel.Children.Add(guidance);
        AddField(panel, "Reference-сервер на этом компьютере", servers);
        AddField(panel, "Каталог сертификатов", folders);
        var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var button in new[] { scan, browse, browseKeys, elevate }) tools.Children.Add(button);
        panel.Children.Add(tools); panel.Children.Add(results); panel.Children.Add(serverState);
        var serviceButtons = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var button in new[] { issue, stop, start, rollback }) serviceButtons.Children.Add(button);
        panel.Children.Add(serviceButtons);
        var navigation = new WrapPanel();
        foreach (var button in new[] { back, next, finish, cancel }) navigation.Children.Add(button);
        panel.Children.Add(navigation);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        servers.SelectionChanged += async (_, _) =>
        {
            if (!initializing && progress.Step == CertificateWizardStep.Inspect && !installed)
                await RunAsync(AnalyzeAsync);
        };
        folders.SelectionChanged += (_, _) => { if (!initializing && !installed) UpdateCurrentFile(); RefreshButtons(); };
        scan.Click += async (_, _) => await RunAsync(AnalyzeAsync);
        browse.Click += async (_, _) =>
        {
            var dialog = new OpenFolderDialog { Title = "Папка с RefSrv.exe" };
            if (dialog.ShowDialog(this) != true) return;
            var exe = Path.Combine(dialog.FolderName, "RefSrv.exe");
            if (!File.Exists(exe)) { MessageBox.Show(this, "В папке не найден RefSrv.exe."); return; }
            initializing = true;
            var list = (servers.ItemsSource as IEnumerable<LocalReferenceServer> ?? []).ToList();
            var item = list.FirstOrDefault(s => SamePath(s.ExePath, exe)) ?? new LocalReferenceServer(exe, null, 0, []);
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
            manualKeys = dialog.FolderName; manualKeysServer = selected?.ExePath;
            await RunAsync(AnalyzeAsync);
        };
        elevate.Click += (_, _) => RelaunchElevated();
        issue.Click += async (_, _) => await RunAsync(PrepareAsync);
        stop.Click += async (_, _) => await RunAsync(async () =>
        {
            if (selected is null) return;
            if (!CertificateToolsLauncher.IsAdministrator) { RelaunchElevated(); return; }
            serviceTouched = true;
            await Task.Run(() => WindowsServiceControl.StopAsync(selected, Token));
            serverState.Text = "Ожидаем полную остановку службы, процесса и портов…";
            timer.Start();
        });
        start.Click += async (_, _) => await RunAsync(async () =>
        {
            if (selected is null) return;
            if (!CertificateToolsLauncher.IsAdministrator) { RelaunchElevated(); return; }
            serviceTouched = true;
            await Task.Run(() => WindowsServiceControl.StartAsync(selected, Token));
            serverState.Text = "Ожидаем запуск службы и проверяем сертификат…";
            timer.Start();
        });
        next.Click += async (_, _) => await RunAsync(AdvanceAsync);
        back.Click += async (_, _) => await RunAsync(async () =>
        {
            progress.Back(); SetStepText();
            if (progress.Step == CertificateWizardStep.Inspect && !installed)
            { prepared = null; newPublic = null; await AnalyzeAsync(); }
            else await ObserveServerAsync();
        });
        cancel.Click += (_, _) => RequestCancel();
        finish.Click += (_, _) => { allowClose = true; Close(); };
        rollback.Click += async (_, _) => await RunAsync(RollbackAsync);
        timer.Tick += async (_, _) => await PollAsync();
        Loaded += async (_, _) => await RunAsync(AnalyzeAsync);
        Closing += (_, e) => { if (!allowClose) { e.Cancel = true; _ = Dispatcher.BeginInvoke(new Action(RequestCancel)); } };
        Closed += (_, _) => { timer.Stop(); lifetime.Cancel(); lifetime.Dispose(); };
        SetStepText(); RefreshButtons();
    }

    private async Task AnalyzeAsync()
    {
        timer.Stop(); currentFile = null; report = null; files = []; selected = null; serviceState = null;
        results.Text = "Поиск установленного Reference-сервера и его сертификата…";
        var found = await WindowsReferenceDiscovery.FindAsync(Token);
        initializing = true;
        try
        {
            var previous = servers.SelectedItem as LocalReferenceServer;
            var previousFolder = folders.SelectedItem as string;
            var list = found.ToList();
            if (previous is not null && !list.Any(s => SamePath(s.ExePath, previous.ExePath))) list.Add(previous);
            servers.ItemsSource = list;
            servers.SelectedItem = previous is null ? null : list.FirstOrDefault(s => SamePath(s.ExePath, previous.ExePath) && s.ServiceName == previous.ServiceName);
            if (servers.SelectedItem is null && list.Count == 1) servers.SelectedIndex = 0;
            selected = servers.SelectedItem as LocalReferenceServer;
            folders.ItemsSource = null;
            if (selected is null)
            {
                results.Text = list.Count == 0 ? "Reference-сервер не найден. Запустите мастер на компьютере, где установлен RefSrv. При необходимости укажите папку RefSrv.exe."
                    : "Найдено несколько Reference-серверов. Выберите нужный в списке.";
                return;
            }
            if (!SamePath(manualKeysServer, selected.ExePath)) manualKeys = null;
            files = await Task.Run(() => CertificateDiscovery.Find(selected.Directory, "localhost"));
            if (manualKeys is not null)
                files = files.Concat(await Task.Run(() => CertificateDiscovery.Find(manualKeys, "localhost"))).DistinctBy(f => f.Path).ToArray();
            serviceState = ReadServiceState();
            serverState.Text = ServiceSummary();
            var directories = files.Select(f => Path.GetDirectoryName(f.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // A live peer is used only to associate the active PEM, never to establish trust by itself.
            if (WindowsReferenceDiscovery.IsProcessRunning(selected.ExePath))
            {
                var expected = directories.Length == 1 ? CertificateDiscovery.CurrentFile(files, directories[0]).Sha256 : null;
                report = await LocalReferenceProbe.FindAsync(selected, expected, Token);
            }
            folders.ItemsSource = directories;
            var matched = report?.Certificate is { } peer ? CertificateDiscovery.MatchKeysDirectory(files, peer.Sha256) : null;
            folders.SelectedItem = matched ?? directories.FirstOrDefault(d => SamePath(d, manualKeys))
                ?? directories.FirstOrDefault(d => SamePath(d, previousFolder)) ?? (directories.Length == 1 ? directories[0] : null);
        }
        finally { initializing = false; }
        UpdateCurrentFile();
    }

    private void UpdateCurrentFile()
    {
        currentFile = null;
        try
        {
            if (folders.SelectedItem is not string keys)
            { results.Text = files.Count == 0 ? "Сертификат не найден. Проверьте права администратора или укажите каталог httpKeys базы выбранного сервера."
                : "Найдено несколько каталогов сертификатов. Выберите каталог базы нужного сервера."; return; }
            currentFile = CertificateDiscovery.CurrentFile(files, keys, report?.Certificate?.Sha256);
            ShowCertificate();
        }
        catch (Exception ex) when (IsExpected(ex)) { results.Text = "Проверка не выполнена: " + SafeMessage(ex); }
    }

    private void ShowCertificate()
    {
        if (currentFile is null) return;
        var now = DateTime.UtcNow;
        var state = now >= currentFile.ValidUntilUtc ? "СЕРТИФИКАТ ПРОСРОЧЕН. Выпустите новый сертификат. «Далее» недоступна."
            : now < currentFile.ValidFromUtc ? "Сертификат ещё не действует. Проверьте дату компьютера или выпустите новый."
            : !currentFile.MatchesHost ? "Срок действителен, но сертификат не подходит для локального подключения. Выпустите новый."
            : "Сертификат действителен.";
        results.Text = $"{state}\n\nФайл: {currentFile.Path}\nИмя: {currentFile.Subject}\n" +
            $"Действует: {currentFile.ValidFromUtc.ToLocalTime():dd.MM.yyyy HH:mm} — {currentFile.ValidUntilUtc.ToLocalTime():dd.MM.yyyy HH:mm}\nSHA256: {currentFile.Sha256}";
        if (currentFile.IsCurrent(now) && currentFile.ValidUntilUtc < now.AddDays(30)) results.Text += "\nСрок истекает менее чем через 30 дней. Можно заранее выпустить новый сертификат.";
        if (report is not null) results.Text += "\n\nHTTPS-порт найден автоматически: " + report.Endpoint.GetLeftPart(UriPartial.Authority);
        if (prepared is not null) results.Text += "\n\nРезервная копия и публичный .cer: " + prepared;
        if (installed) results.Text += "\n\nНовый сертификат уже установлен. «Назад» возвращает к предыдущему шагу, сохраняя выполненные изменения. Для восстановления используйте «Откатить сертификат».";
    }

    private bool CurrentIsValid => currentFile is not null && currentFile.IsCurrent(DateTime.UtcNow) && currentFile.MatchesHost;
    private ReferenceServiceState? ReadServiceState() => selected?.ServiceName is { } name ? WindowsServiceControl.State(name) : null;
    private string ServiceSummary() => selected is null ? "" : $"Служба: {(serviceState is { } state ? StateTitle(state) : "не найдена; запуск вручную")}; процесс: {(WindowsReferenceDiscovery.IsProcessRunning(selected.ExePath) ? "работает" : "остановлен")}.";

    private async Task PrepareAsync()
    {
        if (selected is null || folders.SelectedItem is not string keys || currentFile is null || installed)
            throw new InvalidOperationException("Сначала выберите найденный Reference-сервер и его сертификат.");
        if (!CertificateToolsLauncher.IsAdministrator) { RelaunchElevated(); return; }
        originalFingerprint = currentFile.Sha256;
        var names = WindowsReferenceDiscovery.CertificateNames();
        critical = true;
        try
        {
            prepared = await Task.Run(() => ReferenceCertificateMaintenance.Prepare(selected.Directory, keys, names.FirstOrDefault(n => Uri.CheckHostName(n) == UriHostNameType.Dns) ?? "localhost", names));
            newPublic = await File.ReadAllBytesAsync(Path.Combine(prepared, "rkeeper-local.cer"));
            using var cert = X509CertificateLoader.LoadCertificate(newPublic);
            progress.Prepared(cert.GetCertHashString(HashAlgorithmName.SHA256));
            results.Text = $"Новый сертификат выпущен до {cert.NotAfter.ToLocalTime():dd.MM.yyyy}.\nАдреса компьютера включены автоматически.\nРезервная копия: {prepared}\n\nДля установки необходимо остановить Reference-сервер.";
        }
        finally { critical = false; }
        SetStepText(); timer.Start(); await ObserveServerAsync();
    }

    private async Task AdvanceAsync()
    {
        if (installed && progress.Step is CertificateWizardStep.Inspect or CertificateWizardStep.StopServer)
        {
            VerifyInstalledFile(); progress.ContinueInstalled(); SetStepText(); timer.Start(); await ObserveServerAsync(); return;
        }
        if (progress.Step == CertificateWizardStep.Inspect)
        {
            if (!CurrentIsValid || currentFile is null) throw new InvalidOperationException("Сертификат недействителен. Выпустите новый сертификат.");
            if (!CertificateToolsLauncher.IsAdministrator) { RelaunchElevated(); return; }
            var cert = ReadVerifiedCurrent();
            var added = false;
            critical = true;
            try
            {
                added = CertificateTrust.Install(cert, TrustAddress, currentFile.Sha256, StoreLocation.LocalMachine);
                if (report is not null)
                {
                    report = await new ConnectionPreflight().CheckAsync(report.Endpoint, 4, Token);
                    if (!report.Ready || report.Certificate?.Sha256 != currentFile.Sha256) throw new ConnectionCheckException(report);
                }
                progress.CompleteLocalCertificate(currentFile.ValidFromUtc, currentFile.ValidUntilUtc, DateTime.UtcNow);
            }
            catch { if (added) CertificateTrust.RemoveAdded(cert, StoreLocation.LocalMachine); throw; }
            finally { critical = false; }
            results.Text += "\n\nСрок сертификата проверен, доверие Windows настроено.";
            if (report is null) results.Text += "\nHTTPS-порт сейчас не найден; проверен локальный файл сертификата. Доступность порта программа печати проверит перед входом.";
            SetStepText();
        }
        else if (progress.Step == CertificateWizardStep.StopServer)
        {
            await ObserveServerAsync();
            if (!progress.CanContinue || prepared is null || newPublic is null) throw new InvalidOperationException("Сервер ещё не остановлен полностью.");
            var wrote = false;
            critical = true;
            try
            {
                await Task.Run(() => ReferenceCertificateMaintenance.Apply("install", prepared)); wrote = true;
                trustAdded = CertificateTrust.Install(newPublic, TrustAddress, progress.ExpectedFingerprint, StoreLocation.LocalMachine);
                installed = true; progress.Installed();
                ReloadInstalledFile();
            }
            catch
            {
                if (wrote && !installed) await Task.Run(() => ReferenceCertificateMaintenance.Apply("rollback", prepared));
                throw;
            }
            finally { critical = false; }
            ShowCertificate(); SetStepText();
            serverState.Text = "Сертификат установлен и добавлен в доверенные. Сервер можно включить.";
        }
        else if (progress.Step == CertificateWizardStep.StartServer)
        {
            await ObserveServerAsync(); progress.Complete(); SetStepText();
        }
    }

    private byte[] ReadVerifiedCurrent()
    {
        var file = currentFile ?? throw new InvalidOperationException("Сертификат не проверен.");
        using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(file.Path));
        if (cert.GetCertHashString(HashAlgorithmName.SHA256) != file.Sha256)
            throw new InvalidDataException("Сертификат изменился после проверки. Повторите поиск.");
        CertificateTrust.ValidatePublic(cert.RawData, TrustAddress, file.Sha256);
        return cert.RawData;
    }
    private void ReloadInstalledFile()
    {
        if (prepared is null) throw new InvalidOperationException("Нет данных подготовки.");
        var keys = CertificateFiles.Read(prepared).KeysDirectory;
        files = CertificateDiscovery.Find(keys, "localhost");
        currentFile = CertificateDiscovery.CurrentFile(files, keys, progress.ExpectedFingerprint);
    }
    private void VerifyInstalledFile()
    {
        ReloadInstalledFile();
        if (!CurrentIsValid) throw new InvalidOperationException("Установленный сертификат недействителен. Проверьте дату компьютера.");
        ReadVerifiedCurrent();
    }

    private async Task ObserveServerAsync()
    {
        if (selected is null) return;
        var running = WindowsReferenceDiscovery.IsProcessRunning(selected.ExePath);
        serviceState = ReadServiceState();
        var stopped = serviceState is null ? !running : serviceState == ReferenceServiceState.Stopped;
        // All ports captured for this RefSrv must be released before writing an active certificate.
        var watchedPorts = selected.Ports.Concat(report is null ? [] : new[] { report.Endpoint.Port }).Distinct().ToArray();
        var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        var portOpen = watchedPorts.Any(p => listening.Any(l => l.Port == p));
        observedStopped = stopped && !running && !portOpen;
        if (progress.Step == CertificateWizardStep.Inspect)
        {
            serverState.Text = ServiceSummary();
            if (running && !installed) { await AnalyzeAsync(); }
        }
        else if (progress.Step == CertificateWizardStep.StopServer)
        {
            progress.ObserveStopped(stopped, running, portOpen);
            serverState.Text = installed ? "Сертификат уже установлен. «Далее» вернёт к проверке запуска; повторная установка не выполняется.\n" + ServiceSummary()
                : observedStopped ? "Сервер отключён: служба остановлена, процесса нет, порты освобождены. Можно нажать «Далее»."
                : ServiceSummary() + " Ожидаем полную остановку и освобождение портов…";
        }
        else if (progress.Step == CertificateWizardStep.StartServer)
        {
            if ((serviceState is null || serviceState == ReferenceServiceState.Running) && running)
            {
                var fresh = await WindowsReferenceDiscovery.FindAsync(Token);
                var current = fresh.SingleOrDefault(s => SamePath(s.ExePath, selected.ExePath) && s.ServiceName == selected.ServiceName);
                report = current is null ? null : await LocalReferenceProbe.FindAsync(current, progress.ExpectedFingerprint, Token);
                if (report is not null)
                {
                    progress.ObserveStarted(true, true, report);
                    if (progress.CanContinue) timer.Stop();
                    serverState.Text = progress.CanContinue ? (progress.RolledBack ? "Прежний сертификат восстановлен, сервер запущен." : "Сервер запущен. Новый сертификат действителен, доверен и предъявляется HTTPS-портом. Можно нажать «Далее».") : report.Text;
                    if (report.Certificate?.Sha256 != progress.ExpectedFingerprint) serverState.Text += "\nСервер предъявляет другой сертификат. Настройка ещё не завершена.";
                    return;
                }
            }
            progress.ObserveStarted(false, running, new(TrustAddress, false, false, "Ожидание HTTPS"));
            serverState.Text = "Сервер можно включить. " + ServiceSummary() + " Ожидаем HTTPS-порт с ожидаемым сертификатом.";
        }
    }

    private async Task PollAsync()
    {
        if (busy || polling || closeRequested) return;
        polling = true; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); RefreshButtons();
        try { await ObserveServerAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (IsExpected(ex))
        {
            observedStopped = false;
            if (progress.Step == CertificateWizardStep.StopServer) progress.ObserveStopped(false, true, true);
            if (progress.Step == CertificateWizardStep.StartServer) progress.ObserveStarted(false, false, new(TrustAddress, false, false, "Проверка не выполнена"));
            serverState.Text = "Не удалось проверить состояние: " + SafeMessage(ex);
        }
        finally { operation.Dispose(); operation = null; polling = false; RefreshButtons(); if (closeRequested) ConfirmClose(); }
    }

    private async Task RollbackAsync()
    {
        if (!installed || prepared is null || selected is null) return;
        await ObserveServerAsync();
        if (!observedStopped) throw new InvalidOperationException("Для отката сначала остановите службу. Мастер проверит её остановку.");
        if (MessageBox.Show(this, "Восстановить прежний сертификат? Его исходная ошибка может вернуться.", "Откат", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        critical = true;
        try
        {
            await Task.Run(() => ReferenceCertificateMaintenance.Apply("rollback", prepared));
            installed = false;
            if (trustAdded && newPublic is not null) CertificateTrust.RemoveAdded(newPublic, StoreLocation.LocalMachine);
            trustAdded = false; progress.ExpectRollback(originalFingerprint);
        }
        finally { critical = false; }
        results.Text = "Прежний файл восстановлен. Запустите сервер. Исходная ошибка сертификата может сохраняться. Для выхода нажмите «Отмена».";
        SetStepText(); timer.Start();
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (busy || polling || closeRequested) return;
        busy = true; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); RefreshButtons();
        try { await action(); }
        catch (OperationCanceledException) { if (!closeRequested) results.Text += "\nИстёк таймаут проверки. Повторите поиск."; }
        catch (Exception ex) when (IsExpected(ex)) { results.Text += "\n\nОШИБКА: " + SafeMessage(ex); }
        finally
        {
            initializing = false; critical = false; operation.Dispose(); operation = null; busy = false;
            RefreshButtons(); if (closeRequested) ConfirmClose();
        }
    }

    private void RequestCancel()
    {
        closeRequested = true; timer.Stop();
        if (!critical) operation?.Cancel();
        cancel.Content = "Отмена…";
        if (!busy && !polling) ConfirmClose();
    }
    private void ConfirmClose()
    {
        if (!closeRequested || allowClose) return;
        if (progress.Step != CertificateWizardStep.Complete && (installed || serviceTouched || prepared is not null))
        {
            string state;
            bool stopped;
            try { serviceState = ReadServiceState(); state = ServiceSummary(); stopped = selected is not null && !WindowsReferenceDiscovery.IsProcessRunning(selected.ExePath); }
            catch (Exception ex) when (IsExpected(ex)) { stopped = true; state = "Состояние службы не удалось проверить. Проверьте его в службах Windows."; }
            if (installed || serviceTouched || stopped)
            {
                var message = (installed ? "Сертификат уже установлен и будет сохранён. Для его восстановления используйте «Откатить сертификат».\n" : "Рабочий сертификат не изменён.\n")
                    + state + "\nПри выходе служба автоматически не запускается и не останавливается. Выйти из мастера?";
                if (MessageBox.Show(this, message, "Отмена настройки", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
                { closeRequested = false; cancel.Content = "Отмена"; timer.Start(); RefreshButtons(); return; }
            }
        }
        allowClose = true; Close();
    }
    private void RelaunchElevated()
    {
        CertificateToolsLauncher.Open(); allowClose = true; _ = Dispatcher.BeginInvoke(new Action(Close));
    }

    private void SetStepText()
    {
        heading.Text = progress.Step switch
        {
            CertificateWizardStep.Inspect => "1. Проверка сертификата", CertificateWizardStep.StopServer => installed ? "2. Установка выполнена" : "2. Отключите Reference-сервер",
            CertificateWizardStep.StartServer => "3. Сервер можно включить", _ => "4. Работа завершена"
        };
        guidance.Text = progress.Step switch
        {
            CertificateWizardStep.Inspect => "Мастер сам найдёт локальный Reference-сервер и проверит срок сертификата. IP и порт вводить не нужно. Если сертификат просрочен, выпустите новый.",
            CertificateWizardStep.StopServer => installed ? "Вы вернулись к предыдущему шагу. Выполненная установка сохранена. «Далее» откроет проверку запуска сервера."
                : "Остановите службу кнопкой ниже или штатно вручную. «Далее» появится после полной остановки службы, процесса и освобождения портов.",
            CertificateWizardStep.StartServer => progress.RolledBack ? "Исходный сертификат восстановлен. Запустите сервер; его прежняя ошибка может сохраняться."
                : "Сертификат установлен и добавлен в доверенные. Запустите сервер. Мастер сам найдёт HTTPS-порт и проверит новый сертификат.",
            _ => progress.RolledBack ? "Откат выполнен. Исходная ошибка сертификата может сохраняться." : "Сертификат действителен, доверие Windows настроено. Можно завершить мастер."
        };
        if (progress.Step == CertificateWizardStep.Complete) timer.Stop();
    }

    private void RefreshButtons()
    {
        var inspect = progress.Step == CertificateWizardStep.Inspect;
        var idle = !busy && !polling && !closeRequested;
        servers.IsEnabled = folders.IsEnabled = scan.IsEnabled = browse.IsEnabled = browseKeys.IsEnabled = inspect && !installed && idle;
        elevate.Visibility = CertificateToolsLauncher.IsAdministrator ? Visibility.Collapsed : Visibility.Visible;
        elevate.IsEnabled = idle && !installed;
        issue.Visibility = inspect && !installed && currentFile is not null ? Visibility.Visible : Visibility.Collapsed;
        issue.IsEnabled = idle && selected is not null;
        stop.Visibility = !inspect && progress.Step != CertificateWizardStep.Complete ? Visibility.Visible : Visibility.Collapsed;
        start.Visibility = progress.Step != CertificateWizardStep.Complete && selected is not null ? Visibility.Visible : Visibility.Collapsed;
        stop.IsEnabled = idle && selected?.ServiceName is not null;
        start.IsEnabled = idle && selected?.ServiceName is not null && (progress.Step != CertificateWizardStep.StopServer || installed);
        if (inspect && serviceState != ReferenceServiceState.Stopped) start.IsEnabled = false;
        rollback.Visibility = installed ? Visibility.Visible : Visibility.Collapsed;
        rollback.IsEnabled = idle && observedStopped;
        back.IsEnabled = idle && progress.CanGoBack;
        cancel.IsEnabled = !closeRequested;
        finish.IsEnabled = idle;
        finish.Visibility = progress.Step == CertificateWizardStep.Complete ? Visibility.Visible : Visibility.Collapsed;
        next.Visibility = (progress.RolledBack && report?.Ready != true) || progress.Step == CertificateWizardStep.Complete || (inspect && !CurrentIsValid) ||
            (!inspect && !installed && !progress.CanContinue) ? Visibility.Collapsed : Visibility.Visible;
        next.IsEnabled = idle && (inspect ? CurrentIsValid : installed && progress.Step == CertificateWizardStep.StopServer || progress.CanContinue);
    }

    private static bool SamePath(string? one, string? two) => one is not null && two is not null && one.Equals(two, StringComparison.OrdinalIgnoreCase);
    private static bool IsExpected(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or
        InvalidOperationException or CryptographicException or Win32Exception or System.Text.Json.JsonException;
    private static string SafeMessage(Exception ex) => ex is ConnectionCheckException check ? check.Report.Text :
        ex is Win32Exception win ? "Ошибка Windows: " + win.NativeErrorCode + ". Проверьте права администратора и состояние службы." : ex.Message;
    private static string StateTitle(ReferenceServiceState state) => state switch
    {
        ReferenceServiceState.Stopped => "остановлена", ReferenceServiceState.Running => "запущена", ReferenceServiceState.StartPending => "запускается",
        ReferenceServiceState.StopPending => "останавливается", ReferenceServiceState.Paused => "приостановлена", _ => "переходное состояние"
    };
    private static void AddField(Panel parent, string title, Control input)
    {
        parent.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 5) });
        input.Margin = new Thickness(0, 0, 0, 12); parent.Children.Add(input);
    }
}
