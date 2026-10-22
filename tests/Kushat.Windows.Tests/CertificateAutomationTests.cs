using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kushat.App;
using Kushat.CertificateTool;
using Kushat.Core;
using Xunit;

namespace Kushat.Windows.Tests;

public class CertificateAutomationTests
{
    [Fact]
    public void NativeServiceQueryReadsRealWindowsServiceWithoutChangingIt()
    {
        Assert.InRange((uint)WindowsServiceControl.State("EventLog"), 1u, 7u);
    }

    [Fact]
    public async Task DiscoveryOnlyReturnsReferenceExecutablesAndCanInspectThisWindowsMachine()
    {
        var servers = await WindowsReferenceDiscovery.FindAsync();
        Assert.All(servers, server => Assert.Equal("RefSrv.exe", Path.GetFileName(server.ExePath), ignoreCase: true));
        Assert.True(await WindowsReferenceDiscovery.IsLocalHostAsync("127.0.0.1", CancellationToken.None));
    }

    [Fact]
    public void AutomaticHttpsCandidatesUseOnlySelectedProcessListenersAndRespectBindAddress()
    {
        var server = new LocalReferenceServer("C:\\Test\\RefSrv.exe", "Reference", 123, [8010, 9000],
            [new(123, 8010, "0.0.0.0"), new(123, 9000, "192.168.7.2"), new(123, 8010, "::"), new(456, 9000, "192.168.7.100"), new(123, 9999, "127.0.0.1")]);
        var candidates = WindowsReferenceDiscovery.HttpsCandidates(server);
        Assert.Contains(new Uri("https://127.0.0.1:8010"), candidates);
        Assert.Contains(new Uri("https://[::1]:8010"), candidates);
        Assert.Contains(new Uri("https://192.168.7.2:9000"), candidates);
        Assert.All(candidates, u => Assert.Contains(u.Port, server.Ports));
        Assert.DoesNotContain(candidates, u => u.Host == "192.168.7.100" || u.Port == 9999);
    }

    [Fact]
    public void LocalCertificateNamesIncludeThisComputerWithoutPromptingForIp()
    {
        var names = WindowsReferenceDiscovery.CertificateNames();
        Assert.Contains(Environment.MachineName, names, StringComparer.OrdinalIgnoreCase);
        var generated = LocalCertificate.Create(DateTimeOffset.UtcNow, Environment.MachineName, names);
        using var cert = X509Certificate2.CreateFromPem(generated.Pem);
        Assert.All(names, name => Assert.True(cert.MatchesHostname(name, false, false), name));
    }

    [Fact]
    public void NativePrepareInstallAndRollbackKeepDefaultPemAndRespectFilePermissions()
    {
        var root = Path.Combine(Path.GetTempPath(), "kushat-native-cert-" + Guid.NewGuid().ToString("N"));
        var keys = Path.Combine(root, "base", "httpKeys"); Directory.CreateDirectory(keys);
        var original = LocalCertificate.Create(DateTimeOffset.UtcNow.AddYears(-10)).Pem;
        File.WriteAllText(Path.Combine(root, "RefSrv.exe"), "Test marker only; never executed");
        var defaultFile = Path.Combine(keys, "default.pem"); File.WriteAllText(defaultFile, original);
        try
        {
            var prepared = ReferenceCertificateMaintenance.Prepare(root, keys, "192.168.1.10");
            Assert.False(File.Exists(Path.Combine(keys, "mylocal.pem")));
            ReferenceCertificateMaintenance.Apply("install", prepared);
            var target = Path.Combine(keys, "mylocal.pem");
            var bytes = File.ReadAllBytes(target);
            CertificateFiles.ValidateReplacement(bytes, "192.168.1.10");
            Assert.Equal(original, File.ReadAllText(defaultFile));
            var before = new System.IO.FileInfo(defaultFile).GetAccessControl(System.Security.AccessControl.AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
            var after = new System.IO.FileInfo(target).GetAccessControl(System.Security.AccessControl.AccessControlSections.Access)
                .GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
            Assert.Equal(before, after);
            ReferenceCertificateMaintenance.Apply("rollback", prepared);
            Assert.False(File.Exists(target)); Assert.Equal(original, File.ReadAllText(defaultFile));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task TrustOnlyMatchingPublicCertificateThenStrictProbeSucceeds()
    {
        var generated = LocalCertificate.Create(DateTimeOffset.UtcNow, "127.0.0.1");
        using var pem = X509Certificate2.CreateFromPem(generated.Pem, generated.Pem);
        var pfx = pem.Export(X509ContentType.Pfx);
        using var serverCert = X509CertificateLoader.LoadPkcs12(pfx, null);
        CryptographicOperations.ZeroMemory(pfx);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var endpoint = new Uri($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        var fingerprint = pem.GetCertHashString(HashAlgorithmName.SHA256);
        Assert.Throws<InvalidDataException>(() => CertificateTrust.ValidatePublic(generated.PublicCertificate, endpoint, "different certificate"));
        Assert.Throws<InvalidDataException>(() => CertificateTrust.ValidatePublic(generated.PublicCertificate, new Uri("https://192.168.1.123"), fingerprint));
        var added = false;
        try
        {
            added = CertificateTrust.Install(generated.PublicCertificate, endpoint, fingerprint, StoreLocation.LocalMachine);
            Assert.False(CertificateTrust.Install(generated.PublicCertificate, endpoint, fingerprint, StoreLocation.LocalMachine));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var serving = Task.Run(async () =>
            {
                using var peer = await listener.AcceptTcpClientAsync(timeout.Token);
                using var tls = new SslStream(peer.GetStream());
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = serverCert }, timeout.Token);
            });
            var result = await new ConnectionPreflight().CheckAsync(endpoint, cancellationToken: timeout.Token);
            await serving;
            Assert.True(result.Ready, result.Text); Assert.True(result.PortOpen);
            Assert.Equal(fingerprint, result.Certificate!.Sha256);
        }
        finally { if (added) CertificateTrust.RemoveAdded(generated.PublicCertificate, StoreLocation.LocalMachine); }
    }
}
