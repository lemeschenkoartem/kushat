using System.IO;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;

namespace Kushat.App;

public sealed record ReferenceServiceDefinition(string Name, string ExePath, int ProcessId);
public sealed record ReferenceProcessDefinition(int Id, string ExePath);
public sealed record ReferencePort(int ProcessId, int Port);
public sealed class DiscoverySnapshot
{
    public List<ReferenceServiceDefinition> Services { get; set; } = [];
    public List<ReferenceProcessDefinition> Processes { get; set; } = [];
    public List<ReferencePort> Ports { get; set; } = [];
}
public sealed record LocalReferenceServer(string ExePath, string? ServiceName, int ProcessId, IReadOnlyList<int> Ports)
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
            [pscustomobject]@{ProcessId=[int]$_.OwningProcess; Port=[int]$_.LocalPort}
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
            Path.GetFullPath(s.ExePath), s.Name, s.ProcessId, snapshot.Ports.Where(p => p.ProcessId == s.ProcessId).Select(p => p.Port).Distinct().Order().ToArray())).ToList();
        foreach (var p in snapshot.Processes.Where(p => IsReferenceExecutable(p.ExePath)))
            if (!servers.Any(s => s.ProcessId == p.Id)) servers.Add(new(Path.GetFullPath(p.ExePath), null, p.Id,
                snapshot.Ports.Where(x => x.ProcessId == p.Id).Select(x => x.Port).Distinct().Order().ToArray()));
        return servers;
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
