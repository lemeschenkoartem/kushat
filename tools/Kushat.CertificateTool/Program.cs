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
    if (!OperatingSystem.IsWindows())
        throw new InvalidOperationException("Эта утилита предназначена для Windows.");
    if (args.Length != 2 || args[0] is not ("prepare" or "install" or "rollback"))
        throw new ArgumentException("Команды:\n  prepare \"папка Rk7MidServer\"\n  install \"папка подготовки\"\n  rollback \"папка подготовки\"");
    CertificateMaintenance.Run(args[0], args[1]);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("ОШИБКА: " + ex.Message);
    return 1;
}

[SupportedOSPlatform("windows")]
static class CertificateMaintenance
{
    const string ExpectedCertificate = "E386054B1BBC16C3F4D54C95D03CB1CCD97E317B0B1AE7EC622179A75A447A8B";
    const string Original = "default.before.pem";
    const string Replacement = "default.new.pem";
    record Preparation(string ServerDirectory, string OriginalFileHash, string NewFileHash);

    static string Target(string serverDirectory) => Path.Combine(serverDirectory, "midbase", "httpKeys", "default.pem");
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static void Run(string command, string directory)
    {
        directory = Path.GetFullPath(directory);
        if (command == "prepare")
        {
            Prepare(directory);
            return;
        }
        var manifest = JsonSerializer.Deserialize<Preparation>(
            File.ReadAllText(Path.Combine(directory, "preparation.json")))
            ?? throw new InvalidDataException("Не найдены данные подготовки.");
        var target = Target(manifest.ServerDirectory);
        var original = File.ReadAllBytes(Path.Combine(directory, Original));
        var replacement = File.ReadAllBytes(Path.Combine(directory, Replacement));
        if (Hash(original) != manifest.OriginalFileHash || Hash(replacement) != manifest.NewFileHash)
            throw new InvalidDataException("Файлы подготовки изменились. Замена отменена.");
        using var oldCert = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(original));
        if (oldCert.GetCertHashString(HashAlgorithmName.SHA256) != ExpectedCertificate)
            throw new InvalidDataException("Резервная копия не соответствует проверенному сертификату.");
        if (command == "install") ValidateReplacement(replacement);
        var current = File.ReadAllBytes(target);
        var expectedHash = command == "install" ? manifest.OriginalFileHash : manifest.NewFileHash;
        if (Hash(current) != expectedHash)
            throw new InvalidDataException("Рабочий файл изменился после подготовки. Замена отменена.");
        RequireStopped();
        var requested = command == "install" ? replacement : original;
        // Open the existing file, preserving its owner and ACL (including the RK7 service account).
        // RK7 is stopped; keep a verified backup and restore bytes if an ordinary write fails.
        try
        {
            WriteExisting(target, requested);
            if (Hash(File.ReadAllBytes(target)) != Hash(requested))
                throw new IOException("Не удалось проверить записанный файл.");
        }
        catch
        {
            try { WriteExisting(target, current); }
            catch { Console.Error.WriteLine("Не удалось восстановить файл автоматически. Резервная копия: " + Path.Combine(directory, Original)); }
            throw;
        }
        Console.WriteLine(command == "install"
            ? "Новый сертификат записан. Запустите кассовый сервер штатным способом."
            : "Прежний сертификат восстановлен. Запустите кассовый сервер штатным способом.");
        Console.WriteLine("Службы и хранилища доверия Windows утилита не меняла.");
    }

    static void Prepare(string serverDirectory)
    {
        if (!File.Exists(Path.Combine(serverDirectory, "MIDSERV.exe")))
            throw new DirectoryNotFoundException("В указанной папке не найден MIDSERV.exe.");
        VerifyRunningInstance(serverDirectory);
        var target = Target(serverDirectory);
        var original = File.ReadAllBytes(target);
        using var oldCert = X509Certificate2.CreateFromPem(Encoding.UTF8.GetString(original));
        if (oldCert.GetCertHashString(HashAlgorithmName.SHA256) != ExpectedCertificate)
            throw new InvalidDataException("Рабочий сертификат отличается от проверенного просроченного сертификата. Файл не изменён.");
        var generated = LocalCertificate.Create(DateTimeOffset.UtcNow);
        var newBytes = Encoding.ASCII.GetBytes(generated.Pem);
        ValidateReplacement(newBytes);
        var output = Path.Combine(Path.GetDirectoryName(target)!,
            "Kushat-renewal-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        // Restrict access before writing either old or new private key.
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
        File.WriteAllBytes(Path.Combine(output, Original), original);
        File.WriteAllBytes(Path.Combine(output, Replacement), newBytes);
        File.WriteAllBytes(Path.Combine(output, "rkeeper-local.cer"), generated.PublicCertificate);
        File.WriteAllText(Path.Combine(output, "preparation.json"), JsonSerializer.Serialize(
            new Preparation(serverDirectory, Hash(original), Hash(newBytes)), new JsonSerializerOptions { WriteIndented = true }));
        using var cert = X509CertificateLoader.LoadCertificate(generated.PublicCertificate);
        File.WriteAllText(Path.Combine(output, "certificate-info.txt"),
            $"Subject: {cert.Subject}\nSHA256: {cert.GetCertHashString(HashAlgorithmName.SHA256)}\n" +
            $"NotBefore UTC: {cert.NotBefore.ToUniversalTime():u}\nNotAfter UTC: {cert.NotAfter.ToUniversalTime():u}\n" +
            "SAN: DNS:rk7.local, DNS:localhost, IP:127.0.0.1, IP:::1\n", Encoding.UTF8);
        Console.WriteLine("ПОДГОТОВЛЕНО. Рабочий сертификат не изменён. Сервер не остановлен.");
        Console.WriteLine("Папка подготовки:\n" + output);
        Console.WriteLine("SHA256 нового сертификата: " + cert.GetCertHashString(HashAlgorithmName.SHA256));
        Console.WriteLine("Срок действия UTC: " + cert.NotAfter.ToUniversalTime().ToString("u"));
        Console.WriteLine("Новый PEM содержит закрытый ключ. Не отправляйте PEM-файлы другим людям.");
    }

    static void ValidateReplacement(byte[] pemBytes)
    {
        var pem = Encoding.ASCII.GetString(pemBytes);
        using var cert = X509Certificate2.CreateFromPem(pem, pem);
        if (!cert.HasPrivateKey || cert.NotBefore.ToUniversalTime() > DateTime.UtcNow || cert.NotAfter.ToUniversalTime() <= DateTime.UtcNow)
            throw new InvalidDataException("Новый сертификат или ключ не прошёл проверку.");
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(cert);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // Local self-signed certificate has no CRL service.
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        if (!chain.Build(cert))
            throw new InvalidDataException("Не удалось проверить новый самоподписанный сертификат.");
    }

    static void RequireStopped()
    {
        var processes = Process.GetProcessesByName("MIDSERV");
        var running = processes.Length != 0;
        foreach (var process in processes) process.Dispose();
        if (running)
            throw new InvalidOperationException("MIDSERV запущен. Сначала остановите кассовый сервер штатным способом в согласованное время. Утилита его не останавливает.");
    }

    static void VerifyRunningInstance(string serverDirectory)
    {
        var expected = Path.GetFullPath(Path.Combine(serverDirectory, "MIDSERV.exe"));
        var processes = Process.GetProcessesByName("MIDSERV");
        try
        {
            if (processes.Length != 0 && !processes.Any(process => string.Equals(
                process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Запущенный MIDSERV находится в другой папке. Подготовка отменена: укажите папку работающего сервера.");
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    static void WriteExisting(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None);
        stream.SetLength(0);
        stream.Write(bytes);
        stream.Flush(true);
    }
}
