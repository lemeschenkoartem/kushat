using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;

namespace Kushat.App;

public sealed record ReferenceServiceDefinition(string Name, string ExePath, int ProcessId);
public sealed record ReferenceProcessDefinition(int Id, string ExePath);
public sealed record ReferencePort(int ProcessId, int Port, string Address = "0.0.0.0");
public sealed class DiscoverySnapshot
{
    public List<ReferenceServiceDefinition> Services { get; set; } = [];
    public List<ReferenceProcessDefinition> Processes { get; set; } = [];
    public List<ReferencePort> Ports { get; set; } = [];
}
public sealed record LocalReferenceServer(string ExePath, string? ServiceName, int ProcessId, IReadOnlyList<int> Ports, IReadOnlyList<ReferencePort>? Listeners = null)
{
    public string Directory => Path.GetDirectoryName(ExePath)!;
    public string DisplayName => (ServiceName ?? "Приложение RefSrv") + " · " + ExePath;
}

public static class WindowsReferenceDiscovery
{
    // Read-only local inspection. Only executable paths, service identities, PIDs and ports are returned.
    // Service arguments, INI contents and private keys are never exported.
    private const string DiscoveryScript = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        $rkServices = @(Get-CimInstance Win32_Service | Where-Object { $_.PathName -match '(?i)RefSrv\.exe' } | ForEach-Object {
            $rkMatch = [regex]::Match($_.PathName, '^\s*"?(.+?RefSrv\.exe)(?:"|\s|$)', 'IgnoreCase')
            if ($rkMatch.Success) { [pscustomobject]@{Name=$_.Name; ExePath=$rkMatch.Groups[1].Value; ProcessId=[int]$_.ProcessId} }
        })
        $rkProcesses = @(Get-Process -Name RefSrv -ErrorAction SilentlyContinue | ForEach-Object {
            if ($_.Path) { [pscustomobject]@{Id=$_.Id; ExePath=$_.Path} }
        })
        $rkIds = @($rkProcesses | ForEach-Object { $_.Id })
        $rkPorts = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object { $_.OwningProcess -in $rkIds } | ForEach-Object {
            [pscustomobject]@{ProcessId=[int]$_.OwningProcess; Port=[int]$_.LocalPort; Address=[string]$_.LocalAddress}
        })
        [pscustomobject]@{Services=$rkServices; Processes=$rkProcesses; Ports=$rkPorts} | ConvertTo-Json -Depth 5 -Compress
        """;

    public static async Task<IReadOnlyList<LocalReferenceServer>> FindAsync(CancellationToken ct = default)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(DiscoveryScript)) }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить поиск Reference-сервера.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; } // Only our own inspection process.
        var text = await output; await errors;
        if (process.ExitCode != 0 || text.Length > 1_000_000) throw new IOException("Поиск не выполнен. Запустите мастер от администратора.");
        var snapshot = JsonSerializer.Deserialize<DiscoverySnapshot>(text.TrimStart('\ufeff'), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new IOException("Поиск не вернул данные.");
        var servers = snapshot.Services.Where(s => IsReferenceExecutable(s.ExePath)).Select(s => new LocalReferenceServer(
            Path.GetFullPath(s.ExePath), s.Name, s.ProcessId, snapshot.Ports.Where(p => p.ProcessId == s.ProcessId).Select(p => p.Port).Distinct().Order().ToArray(), snapshot.Ports.Where(p => p.ProcessId == s.ProcessId).ToArray())).ToList();
        foreach (var p in snapshot.Processes.Where(p => IsReferenceExecutable(p.ExePath)))
            if (!servers.Any(s => s.ProcessId == p.Id)) servers.Add(new(Path.GetFullPath(p.ExePath), null, p.Id,
                snapshot.Ports.Where(x => x.ProcessId == p.Id).Select(x => x.Port).Distinct().Order().ToArray(), snapshot.Ports.Where(x => x.ProcessId == p.Id).ToArray()));
        return servers;
    }

    public static IReadOnlyList<Uri> HttpsCandidates(LocalReferenceServer server)
    {
        var listeners = server.Listeners ?? server.Ports.Select(p => new ReferencePort(server.ProcessId, p)).ToArray();
        return listeners.Where(p => p.ProcessId == server.ProcessId && server.Ports.Contains(p.Port)).Select(p =>
        {
            var ip = IPAddress.Parse(p.Address);
            var host = ip.Equals(IPAddress.Any) ? IPAddress.Loopback : ip.Equals(IPAddress.IPv6Any) ? IPAddress.IPv6Loopback : ip;
            return new UriBuilder("https", host.ToString(), p.Port).Uri;
        }).Distinct().OrderBy(u => u.Port is 8010 or 8086 ? 0 : 1).ThenBy(u => u.Port).ToArray();
    }

    public static IReadOnlyList<string> CertificateNames()
    {
        var network = IPGlobalProperties.GetIPGlobalProperties();
        var fqdn = string.IsNullOrEmpty(network.DomainName) || network.HostName.EndsWith("." + network.DomainName, StringComparison.OrdinalIgnoreCase)
            ? network.HostName : network.HostName + "." + network.DomainName;
        return new[] { Environment.MachineName, fqdn }.Concat(NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up).SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address).Where(a => !a.IsIPv6LinkLocal && !a.IsIPv6Multicast && !a.Equals(IPAddress.Any) && !a.Equals(IPAddress.IPv6Any))
            .Select(a => a.ToString())).Where(h => Uri.CheckHostName(h) != UriHostNameType.Unknown)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static bool IsReferenceExecutable(string path) => Path.GetFileName(path).Equals("RefSrv.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    public static bool IsProcessRunning(string executable)
    {
        var processes = Process.GetProcessesByName("RefSrv");
        try
        {
            foreach (var process in processes)
            {
                var path = process.MainModule?.FileName ?? throw new IOException("Не удалось проверить процесс RefSrv.");
                if (Path.GetFullPath(path).Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static async Task<bool> IsLocalHostAsync(string host, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        var local = NetworkInterface.GetAllNetworkInterfaces().SelectMany(i => i.GetIPProperties().UnicastAddresses).Select(a => a.Address).ToArray();
        return addresses.Length > 0 && addresses.All(a => IPAddress.IsLoopback(a) || local.Contains(a));
    }
}
