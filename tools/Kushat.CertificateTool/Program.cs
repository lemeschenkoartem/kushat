using System.Text;
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
