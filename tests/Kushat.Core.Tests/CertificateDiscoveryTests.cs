using Kushat.CertificateTool;
using Xunit;

namespace Kushat.Core.Tests;

public sealed class CertificateDiscoveryTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "kushat-search-" + Guid.NewGuid().ToString("N"));
    public CertificateDiscoveryTests() => Directory.CreateDirectory(root);

    [Fact]
    public void FindsDefaultAndCustomCertificatesWithoutInspectingBackupsOrStagedKeys()
    {
        var keys = Path.Combine(root, "base", "httpKeys"); Directory.CreateDirectory(keys);
        var valid = LocalCertificate.Create(DateTimeOffset.UtcNow, "192.168.1.10");
        File.WriteAllText(Path.Combine(keys, "mylocal.pem"), valid.Pem);
        var expired = LocalCertificate.Create(DateTimeOffset.UtcNow.AddYears(-10));
        File.WriteAllText(Path.Combine(keys, "default.pem"), expired.Pem);
        var backup = Path.Combine(root, "BACKUPS", "httpKeys"); Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "default.pem"), valid.Pem);
        var staged = Path.Combine(keys, "Kushat-reference-test", "httpKeys"); Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "default.pem"), valid.Pem);
        var files = CertificateDiscovery.Find(root, "192.168.1.10");
        Assert.Equal(2, files.Count);
        Assert.True(files.Single(f => Path.GetFileName(f.Path) == "mylocal.pem").IsCurrent(DateTime.UtcNow));
        var old = files.Single(f => Path.GetFileName(f.Path) == "default.pem");
        Assert.False(old.IsCurrent(DateTime.UtcNow)); Assert.False(old.MatchesHost);
        Assert.Equal(keys, CertificateDiscovery.MatchKeysDirectory(files, files.Single(f => f.MatchesHost).Sha256));
    }

    [Fact]
    public void SameCertificateInTwoLocationsIsAmbiguousInsteadOfChoosingFirst()
    {
        var generated = LocalCertificate.Create(DateTimeOffset.UtcNow);
        foreach (var name in new[] { "one", "two" })
        {
            var keys = Path.Combine(root, name, "httpKeys"); Directory.CreateDirectory(keys);
            File.WriteAllText(Path.Combine(keys, "default.pem"), generated.Pem);
        }
        var files = CertificateDiscovery.Find(root, "localhost");
        Assert.Null(CertificateDiscovery.MatchKeysDirectory(files, files[0].Sha256));
    }

    public void Dispose() => Directory.Delete(root, true);
}
