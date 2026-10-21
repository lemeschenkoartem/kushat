using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kushat.App;

public static class CertificateTrust
{
    public static void ValidatePublic(byte[] der, Uri endpoint, string expectedFingerprint)
    {
        using var cert = X509CertificateLoader.LoadCertificate(der);
        if (cert.GetCertHashString(HashAlgorithmName.SHA256) != expectedFingerprint)
            throw new InvalidDataException("Публичный сертификат не совпадает с сертификатом выбранного сервера.");
        if (!cert.SubjectName.RawData.SequenceEqual(cert.IssuerName.RawData) || cert.HasPrivateKey ||
            cert.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority))
            throw new InvalidDataException("Мастер добавляет только публичный самоподписанный серверный сертификат. Для корпоративного УЦ настройте его доверенную цепочку.");
        if (DateTime.UtcNow < cert.NotBefore.ToUniversalTime() || DateTime.UtcNow >= cert.NotAfter.ToUniversalTime() ||
            !cert.MatchesHostname(endpoint.IdnHost, false, false))
            throw new InvalidDataException("Сертификат просрочен, ещё не действует или не подходит к адресу. Добавление доверия это не исправит.");
        if (!cert.Extensions.OfType<X509EnhancedKeyUsageExtension>().Any(e => e.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value == "1.3.6.1.5.5.7.3.1")))
            throw new InvalidDataException("В сертификате отсутствует назначение Server Authentication.");
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(cert);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        if (!chain.Build(cert)) throw new InvalidDataException("Проверка сертификата не пройдена.");
    }

    public static bool Install(byte[] der, Uri endpoint, string expectedFingerprint, StoreLocation location)
    {
        ValidatePublic(der, endpoint, expectedFingerprint);
        using var cert = X509CertificateLoader.LoadCertificate(der);
        using var store = new X509Store(StoreName.Root, location);
        store.Open(OpenFlags.ReadWrite);
        if (store.Certificates.Cast<X509Certificate2>().Any(c => c.RawData.SequenceEqual(der))) return false;
        store.Add(cert);
        return true;
    }

    public static void RemoveAdded(byte[] der, StoreLocation location)
    {
        using var cert = X509CertificateLoader.LoadCertificate(der);
        using var store = new X509Store(StoreName.Root, location);
        store.Open(OpenFlags.ReadWrite); store.Remove(cert);
    }
}
