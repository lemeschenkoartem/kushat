using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Kushat.CertificateTool;

Console.OutputEncoding = Encoding.UTF8;
try
{
    if (args is ["--version"]) { Console.WriteLine("Kushat.CertificateTool 2.0.0 — Reference / mylocal.pem"); return 0; }
    if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Эта утилита предназначена для Windows.");
    if (args is ["prepare", var server, var keys, var host])
        ReferenceCertificateMaintenance.Prepare(server, keys, host);
    else if (args is ["install" or "rollback", var directory])
        ReferenceCertificateMaintenance.Apply(args[0], directory);
    else throw new ArgumentException("Версия 2.0.0, только Reference-сервер / mylocal.pem.\nКоманды:\n" +
        "  prepare \"папка RefSrv.exe\" \"папка httpKeys\" IP-или-DNS\n" +
        "  install \"папка подготовки версии 2\"\n  rollback \"папка подготовки версии 2\"");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("ОШИБКА: " + ex.Message);
    return 1;
}

[SupportedOSPlatform("windows")]
static class ReferenceCertificateMaintenance
{
    public static void Prepare(string serverDirectory, string keysDirectory, string host)
    {
        serverDirectory = Path.GetFullPath(serverDirectory);
        keysDirectory = Path.GetFullPath(keysDirectory);
        VerifyServer(serverDirectory, requireStopped: false);
        var defaultBytes = File.ReadAllBytes(Path.Combine(keysDirectory, "default.pem"));
        using var defaultCert = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(defaultBytes));
        var custom = Path.Combine(keysDirectory, "mylocal.pem");
        var original = File.Exists(custom) ? File.ReadAllBytes(custom) : null;
        var generated = LocalCertificate.Create(DateTimeOffset.UtcNow, host);
        var replacement = Encoding.ASCII.GetBytes(generated.Pem);
        CertificateFiles.ValidateReplacement(replacement, host);
        var output = Path.Combine(keysDirectory,
            "Kushat-reference-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        var identities = new[] { WindowsIdentity.GetCurrent().User!,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) };
        foreach (var identity in identities)
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(output).Create(security);
        if (original is not null) File.WriteAllBytes(Path.Combine(output, CertificateFiles.Backup), original);
        File.WriteAllBytes(Path.Combine(output, CertificateFiles.Replacement), replacement);
        File.WriteAllBytes(Path.Combine(output, "rkeeper-local.cer"), generated.PublicCertificate);
        var state = new CertificateFiles.Preparation(2, serverDirectory, keysDirectory, host,
            CertificateFiles.Hash(defaultBytes), original is null ? null : CertificateFiles.Hash(original), CertificateFiles.Hash(replacement));
        File.WriteAllText(Path.Combine(output, CertificateFiles.ManifestName), JsonSerializer.Serialize(
            state, new JsonSerializerOptions { WriteIndented = true }));
        using var cert = X509CertificateLoader.LoadCertificate(generated.PublicCertificate);
        var info = $"Subject: {cert.Subject}\nSHA256: {cert.GetCertHashString(HashAlgorithmName.SHA256)}\n" +
            $"NotBefore UTC: {cert.NotBefore.ToUniversalTime():u}\nNotAfter UTC: {cert.NotAfter.ToUniversalTime():u}\n" +
            $"SAN: {host}, rk7.local, localhost, 127.0.0.1, ::1\nTarget: {custom}\n";
        File.WriteAllText(Path.Combine(output, "certificate-info.txt"), info, Encoding.UTF8);
        Console.WriteLine("ПОДГОТОВЛЕНО. Рабочие PEM не изменены; сервер не остановлен.");
        Console.WriteLine("Папка подготовки:\n" + output);
        Console.WriteLine(info);
        Console.WriteLine("PEM содержат закрытые ключи. Не передавайте их. Для доверия используется только rkeeper-local.cer.");
    }

    public static void Apply(string command, string directory)
    {
        directory = Path.GetFullPath(directory);
        CertificateFiles.Apply(directory, command == "rollback",
            state => VerifyServer(state.ServerDirectory, requireStopped: true),
            (target, template) => new FileInfo(target).Create(FileMode.CreateNew,
                FileSystemRights.Read | FileSystemRights.Write, FileShare.None, 4096, FileOptions.None,
                new FileInfo(template).GetAccessControl(AccessControlSections.Access)));
        Console.WriteLine(command == "install"
            ? "Установлен mylocal.pem. Запустите RefSrv штатным способом и проверьте сертификат HTTPS."
            : "Прежнее состояние mylocal.pem восстановлено. Запустите RefSrv штатным способом.");
        Console.WriteLine("default.pem, службы и хранилища доверия Windows не изменены.");
    }

    static void VerifyServer(string directory, bool requireStopped)
    {
        var expected = Path.GetFullPath(Path.Combine(directory, "RefSrv.exe"));
        if (!File.Exists(expected)) throw new FileNotFoundException("В папке не найден RefSrv.exe.");
        var processes = Process.GetProcessesByName("RefSrv");
        try
        {
            var matching = processes.Any(process => string.Equals((process.MainModule?.FileName ?? throw new InvalidOperationException("Не удалось определить путь процесса RefSrv; операция отменена.")), expected, StringComparison.OrdinalIgnoreCase));
            if (requireStopped && matching)
                throw new InvalidOperationException("Этот RefSrv запущен. Остановите его штатным способом в согласованное время; утилита сервер не останавливает.");
            if (!requireStopped && processes.Length != 0 && !matching)
                throw new InvalidOperationException("Работающий RefSrv находится в другой папке. Укажите папку нужного процесса.");
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }
}
