using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kushat.CertificateTool;

public sealed record CertificateFile(string Path, string Sha256, DateTime ValidFromUtc, DateTime ValidUntilUtc,
    bool MatchesHost, string Subject)
{
    public bool IsCurrent(DateTime utcNow) => ValidFromUtc <= utcNow && utcNow < ValidUntilUtc;
}

public static class CertificateDiscovery
{
    public static IReadOnlyList<CertificateFile> Find(string serverDirectory, string host)
    {
        var result = new List<CertificateFile>();
        var pending = new Stack<(string Directory, int Depth)>();
        pending.Push((System.IO.Path.GetFullPath(serverDirectory), 0));
        while (pending.TryPop(out var entry))
        {
            try
            {
                if (new DirectoryInfo(entry.Directory).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (System.IO.Path.GetFileName(entry.Directory).Equals("httpKeys", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var name in new[] { "mylocal.pem", "default.pem" })
                    {
                        var path = System.IO.Path.Combine(entry.Directory, name);
                        if (!File.Exists(path)) continue;
                        try
                        {
                            using var cert = X509Certificate2.CreateFromPem(File.ReadAllText(path));
                            result.Add(new(path, cert.GetCertHashString(HashAlgorithmName.SHA256), cert.NotBefore.ToUniversalTime(),
                                cert.NotAfter.ToUniversalTime(), cert.MatchesHostname(host, false, false), cert.Subject));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException) { }
                    }
                    continue; // Never inspect backups or staged private-key directories inside httpKeys.
                }
                if (entry.Depth >= 6) continue;
                foreach (var child in Directory.EnumerateDirectories(entry.Directory))
                {
                    var name = System.IO.Path.GetFileName(child);
                    if (name.StartsWith("Kushat-", StringComparison.OrdinalIgnoreCase) || name.Equals("BACKUPS", StringComparison.OrdinalIgnoreCase)) continue;
                    pending.Push((child, entry.Depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    public static string? MatchKeysDirectory(IEnumerable<CertificateFile> files, string fingerprint)
    {
        var matches = files.Where(f => f.Sha256.Equals(fingerprint, StringComparison.OrdinalIgnoreCase))
            .Select(f => System.IO.Path.GetDirectoryName(f.Path)!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
