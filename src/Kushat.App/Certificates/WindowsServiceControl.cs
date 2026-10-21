using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Kushat.App;

public enum ReferenceServiceState : uint { Stopped = 1, StartPending = 2, StopPending = 3, Running = 4, ContinuePending = 5, PausePending = 6, Paused = 7 }

public static class WindowsServiceControl
{
    public static ReferenceServiceState State(string serviceName)
    {
        using var manager = Manager(); using var service = Service(manager, serviceName, 4);
        if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return (ReferenceServiceState)status.CurrentState;
    }

    public static async Task RequireDefinitionAsync(LocalReferenceServer server)
    {
        if (server.ServiceName is null) throw new InvalidOperationException("У этого RefSrv не найдена служба Windows.");
        var definitions = await WindowsReferenceDiscovery.FindAsync();
        if (!definitions.Any(d => d.ServiceName == server.ServiceName && d.ExePath.Equals(server.ExePath, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Определение службы изменилось. Повторите поиск.");
    }

    public static async Task StopAsync(LocalReferenceServer server)
    {
        await RequireDefinitionAsync(server);
        using var manager = Manager(); using var service = Service(manager, server.ServiceName!, 4 | 32);
        if (State(server.ServiceName!) == ReferenceServiceState.Stopped) return;
        if (!ControlService(service, 1, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static async Task StartAsync(LocalReferenceServer server)
    {
        await RequireDefinitionAsync(server);
        using var manager = Manager(); using var service = Service(manager, server.ServiceName!, 4 | 16);
        if (State(server.ServiceName!) == ReferenceServiceState.Running) return;
        if (!StartService(service, 0, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private static ServiceHandle Manager()
    {
        var handle = OpenSCManager(null, null, 1);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return handle;
    }
    private static ServiceHandle Service(ServiceHandle manager, string name, uint access)
    {
        var handle = OpenService(manager, name, access);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return handle;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatusProcess
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    {
        public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint;
    }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(ServiceHandle service, int level, out ServiceStatusProcess status, int size, out int needed);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ControlService(ServiceHandle service, uint control, out ServiceStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool StartService(ServiceHandle service, uint count, IntPtr arguments);
}
