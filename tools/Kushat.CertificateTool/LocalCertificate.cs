using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kushat.CertificateTool;

public static class LocalCertificate
{
    public static (string Pem, byte[] PublicCertificate) Create(DateTimeOffset now, string host = "rk7.local")
    {
        if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains('*'))
            throw new ArgumentException("Укажите IP или DNS-имя без протокола и порта.", nameof(host));
        using var key = RSA.Create(2048);
        var subject = new X500DistinguishedNameBuilder();
        subject.AddCommonName(host);
        var request = new CertificateRequest(subject.Build(), key,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("rk7.local");
        names.AddDnsName("localhost");
        names.AddIpAddress(IPAddress.Loopback);
        names.AddIpAddress(IPAddress.IPv6Loopback);
        if (IPAddress.TryParse(host, out var address))
        {
            if (!address.Equals(IPAddress.Loopback) && !address.Equals(IPAddress.IPv6Loopback))
                names.AddIpAddress(address);
        }
        else if (host is not ("rk7.local" or "localhost")) names.AddDnsName(host);
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
