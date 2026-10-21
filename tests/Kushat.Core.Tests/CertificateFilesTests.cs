using System.Text;
using System.Text.Json;
using Kushat.CertificateTool;
using Xunit;

namespace Kushat.Core.Tests;

public sealed class CertificateFilesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "kushat-cert-test-" + Guid.NewGuid().ToString("N"));
    string Prepared => Path.Combine(root, "prepared");
    string Target => Path.Combine(root, "mylocal.pem");
    string Default => Path.Combine(root, "default.pem");
    static readonly byte[] Previous = "previous custom PEM"u8.ToArray();
    static readonly byte[] DefaultBytes = "vendor default PEM"u8.ToArray();
    readonly byte[] replacement;

    public CertificateFilesTests()
    {
        Directory.CreateDirectory(Prepared);
        replacement = Encoding.ASCII.GetBytes(LocalCertificate.Create(DateTimeOffset.UtcNow, "192.168.7.2").Pem);
        File.WriteAllBytes(Default, DefaultBytes);
    }

    void Prepare(bool existing)
    {
        if (existing)
        {
            File.WriteAllBytes(Target, Previous);
            File.WriteAllBytes(Path.Combine(Prepared, CertificateFiles.Backup), Previous);
        }
        File.WriteAllBytes(Path.Combine(Prepared, CertificateFiles.Replacement), replacement);
        File.WriteAllText(Path.Combine(Prepared, CertificateFiles.ManifestName), JsonSerializer.Serialize(
            new CertificateFiles.Preparation(2, root, root, "192.168.7.2", CertificateFiles.Hash(DefaultBytes),
                existing ? CertificateFiles.Hash(Previous) : null, CertificateFiles.Hash(replacement))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstallAndRollbackPreserveDefaultAndRestorePriorCustomState(bool existing)
    {
        Prepare(existing);
        var checks = 0;
        var aclChecks = 0;
        CertificateFiles.Apply(Prepared, false, _ => checks++, (target, _) =>
        {
            aclChecks++;
            return new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
        });
        Assert.Equal(replacement, File.ReadAllBytes(Target));
        Assert.Equal(DefaultBytes, File.ReadAllBytes(Default));
        CertificateFiles.Apply(Prepared, true, _ => checks++, (_, _) => throw new Exception("Rollback must not create a file"));
        if (existing) Assert.Equal(Previous, File.ReadAllBytes(Target));
        else Assert.False(File.Exists(Target));
        Assert.Equal(DefaultBytes, File.ReadAllBytes(Default));
        Assert.Equal(2, checks);
        Assert.Equal(existing ? 0 : 1, aclChecks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunningServerBlocksInstallBeforeMutation(bool existing)
    {
        Prepare(existing);
        Assert.Throws<InvalidOperationException>(() => CertificateFiles.Apply(Prepared, false,
            _ => throw new InvalidOperationException("Server is running"), CreateNew));
        if (existing) Assert.Equal(Previous, File.ReadAllBytes(Target));
        else Assert.False(File.Exists(Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConcurrentCustomChangeIsNotOverwritten(bool existing)
    {
        Prepare(existing);
        File.WriteAllText(Target, "another configuration");
        var failure = Record.Exception(() => CertificateFiles.Apply(Prepared, false, _ => { }, CreateNew));
        if (existing) Assert.IsType<InvalidDataException>(failure);
        else Assert.IsType<IOException>(failure);
        Assert.Equal("another configuration", File.ReadAllText(Target));
    }

    [Fact]
    public void RollbackDoesNotDeleteExternallyChangedCustomCertificate()
    {
        Prepare(false);
        CertificateFiles.Apply(Prepared, false, _ => { }, CreateNew);
        File.WriteAllText(Target, "changed after install");
        Assert.Throws<InvalidDataException>(() => CertificateFiles.Apply(Prepared, true, _ => { }, CreateNew));
        Assert.Equal("changed after install", File.ReadAllText(Target));
    }

    [Fact]
    public void AccessControlFailureLeavesNoNewPrivateKeyAndDoesNotChangeDefault()
    {
        Prepare(false);
        Assert.Throws<UnauthorizedAccessException>(() => CertificateFiles.Apply(Prepared, false, _ => { },
            (_, _) => throw new UnauthorizedAccessException()));
        Assert.False(File.Exists(Target));
        Assert.Equal(DefaultBytes, File.ReadAllBytes(Default));
    }

    [Theory]
    [InlineData("default.pem")]
    [InlineData("prepared/mylocal.new.pem")]
    [InlineData("prepared/mylocal.before.pem")]
    public void TamperedInputsPreventMutation(string relative)
    {
        Prepare(true);
        File.WriteAllText(Path.Combine(root, relative), "tampered");
        Assert.Throws<InvalidDataException>(() => CertificateFiles.Apply(Prepared, false, _ => { }, CreateNew));
        Assert.Equal(Previous, File.ReadAllBytes(Target));
    }

    [Fact]
    public void LegacyPreparationIsRejected()
    {
        File.WriteAllText(Path.Combine(Prepared, CertificateFiles.ManifestName), "{\"ServerDirectory\":\"legacy\"}");
        Assert.Throws<InvalidDataException>(() => CertificateFiles.Apply(Prepared, false, _ => { }, CreateNew));
        Assert.False(File.Exists(Target));
        Assert.Equal(DefaultBytes, File.ReadAllBytes(Default));
    }

    static FileStream CreateNew(string target, string _) =>
        new(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);

    public void Dispose() => Directory.Delete(root, true);
}
