using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows;
using Kushat.Core;
using Microsoft.Win32;

namespace Kushat.App;

public static class CertificateClientTrust
{
    public static async Task<ConnectionReport?> ImportAsync(Window owner, string serverAddress, CancellationToken cancellationToken)
    {
        var endpoint = new ReferenceConnection { ServerAddress = serverAddress }.GetServerEndpoint();
        if (endpoint.Scheme != "https") throw new ArgumentException("Для доверия сертификату укажите HTTPS-адрес Reference-сервера.");
        var dialog = new OpenFileDialog { Title = "Публичный .cer с Reference-сервера", Filter = "Сертификат (*.cer)|*.cer" };
        if (dialog.ShowDialog(owner) != true) return null;
        var bytes = await File.ReadAllBytesAsync(dialog.FileName, cancellationToken);
        var report = await new ConnectionPreflight().CheckAsync(endpoint, 8, cancellationToken);
        var fingerprint = report.Certificate?.Sha256 ?? throw new InvalidOperationException("Порт не предъявил HTTPS-сертификат.");
        var location = CertificateToolsLauncher.IsAdministrator ? StoreLocation.LocalMachine : StoreLocation.CurrentUser;
        var added = CertificateTrust.Install(bytes, endpoint, fingerprint, location);
        try
        {
            report = await new ConnectionPreflight().CheckAsync(endpoint, 8, cancellationToken);
            if (!report.Ready || report.Certificate?.Sha256 != fingerprint) throw new ConnectionCheckException(report);
            return report;
        }
        catch { if (added) CertificateTrust.RemoveAdded(bytes, location); throw; }
    }
}
