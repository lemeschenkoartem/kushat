using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kushat.CertificateTool;

public static class LocalCertificate
{
    public static (string Pem, byte[] PublicCertificate) Create(DateTimeOffset now, string host = "rk7.local", IEnumerable<string>? additionalHosts = null)
    {
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains('*'))
            throw new ArgumentException("Укажите IP или DNS-имя без протокола и порта.", nameof(host));
        using var key = RSA.Create(2048);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(host);
        var request = new CertificateRequest(subject.Build(), key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        var hosts = new[] { "rk7.local", "localhost", "127.0.0.1", "::1", host }
            .Concat(additionalHosts ?? []).Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (var name in hosts)
        {
            if (Uri.CheckHostName(name) == UriHostNameType.Unknown || name.Contains('*'))
                throw new ArgumentException("Некорректное имя или IP компьютера: " + name);
            if (IPAddress.TryParse(name, out var address)) names.AddIpAddress(address);
            else names.AddDnsName(name);
        }
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var cert = request.CreateSelfSigned(now.AddMinutes(-5), now.AddYears(2));
        // Same PEM block types and order as the customer's existing RK7 file.
        var pem = key.ExportRSAPrivateKeyPem() + "\n" + cert.ExportCertificatePem() + "\n";
        return (pem, cert.Export(X509ContentType.Cert));
    }
}
