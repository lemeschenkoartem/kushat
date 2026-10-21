using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kushat.Core.Tests;

internal static class TlsTestCertificate
{
    public static X509Certificate2 ForServer(X509Certificate2 source)
    {
        // Schannel cannot use the ephemeral key produced by CreateFromPem/CreateSelfSigned.
        // Import a transient platform-backed key. DefaultKeySet cleans it up on certificate disposal.
        var pfx = source.Export(X509ContentType.Pfx);
        try { return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.DefaultKeySet); }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }
}
