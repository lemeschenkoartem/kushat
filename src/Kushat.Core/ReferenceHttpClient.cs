using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kushat.Core;

public sealed record CertificateFailure(SslPolicyErrors Errors, X509ChainStatusFlags ChainErrors,
    string DnsName, string Sha256, DateTime? ValidFromUtc, DateTime? ValidUntilUtc);

public static class ReferenceHttpClient
{
    internal static readonly HttpRequestOptionsKey<CertificateFailure> CertificateFailureKey = new("Kushat.CertificateFailure");

    public static HttpClient Create()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
            {
                if (errors != SslPolicyErrors.None)
                {
                    var flags = chain?.ChainStatus.Aggregate(X509ChainStatusFlags.NoError,
                        (result, item) => result | item.Status) ?? X509ChainStatusFlags.NoError;
                    request.Options.Set(CertificateFailureKey, new CertificateFailure(errors, flags,
                        certificate?.GetNameInfo(X509NameType.DnsName, false) ?? "",
                        certificate?.GetCertHashString(HashAlgorithmName.SHA256) ?? "",
                        certificate?.NotBefore.ToUniversalTime(), certificate?.NotAfter.ToUniversalTime()));
                }
                // Observe platform validation without accepting an invalid certificate.
                return errors == SslPolicyErrors.None;
            }
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
}
