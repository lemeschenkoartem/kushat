using System.IO;
using System.Diagnostics;
using System.Windows;

namespace Kushat.App;

public static class CertificateToolsLauncher
{
    public static bool IsAdministrator => new System.Security.Principal.WindowsPrincipal(
        System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    public static void Open(string address, bool elevate = true)
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь программы.");
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Для запуска мастера используйте опубликованный Kushat.exe.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = elevate ? "runas" : "" };
        start.ArgumentList.Add("--certificates");
        if (!string.IsNullOrWhiteSpace(address)) { start.ArgumentList.Add("--address"); start.ArgumentList.Add(address); }
        Process.Start(start);
    }

    public static void OpenFrom(Window owner, string address)
    {
        try { Open(address); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { MessageBox.Show(owner, "Мастер не запущен. Для настройки сертификата и управления службой подтвердите запуск от администратора.", "Настройка соединения"); }
    }
}
