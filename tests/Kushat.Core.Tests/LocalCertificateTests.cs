using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kushat.CertificateTool;
using Xunit;

namespace Kushat.Core.Tests;

public class LocalCertificateTests
{
    [Fact]
    public void PemContainsMatchingPkcs1KeyAndPublicCertificateWithCurrentValidity()
    {
        var now = DateTimeOffset.UtcNow;
        var generated = LocalCertificate.Create(now);
        Assert.StartsWith("-----BEGIN RSA PRIVATE KEY-----", generated.Pem);
        using var certificate = X509Certificate2.CreateFromPem(generated.Pem, generated.Pem);
        Assert.True(certificate.HasPrivateKey);
        Assert.Equal(generated.PublicCertificate, certificate.Export(X509ContentType.Cert));
        Assert.Equal(now.AddYears(2).ToUnixTimeSeconds(), new DateTimeOffset(certificate.NotAfter.ToUniversalTime()).ToUnixTimeSeconds());
        Assert.True(certificate.NotBefore.ToUniversalTime() < now.UtcDateTime);
        using var key = certificate.GetRSAPrivateKey()!;
        using var publicKey = certificate.GetRSAPublicKey()!;
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        Assert.True(publicKey.VerifyData(challenge, key.SignData(challenge, HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var constraints = certificate.Extensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.False(constraints.CertificateAuthority);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("rk7.local")]
    [InlineData("localhost")]
    [InlineData("::1")]
    [InlineData("192.168.7.2")]
    public async Task CertificatePassesRealTlsHostnameAndChainValidation(string host)
    {
        var generated = LocalCertificate.Create(DateTimeOffset.UtcNow, host);
        using var certificate = X509Certificate2.CreateFromPem(generated.Pem, generated.Pem);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serverTask = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token);
            using var tls = new SslStream(socket.GetStream());
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12
            }, timeout.Token);
        });
        using var client = new TcpClient();
        await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, timeout.Token);
        using var stream = new SslStream(client.GetStream());
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck
        };
        policy.CustomTrustStore.Add(certificate);
        policy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        // Real TLS verification with an explicitly trusted self-signed certificate; no callback bypass.
        await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            CertificateChainPolicy = policy,
            EnabledSslProtocols = SslProtocols.Tls12
        }, timeout.Token);
        await serverTask;
        Assert.True(stream.IsAuthenticated);
    }

    [Fact]
    public void ReplacementRequiresRequestedIpInSan()
    {
        var generated = LocalCertificate.Create(DateTimeOffset.UtcNow, "192.168.7.2");
        var pem = System.Text.Encoding.ASCII.GetBytes(generated.Pem);
        CertificateFiles.ValidateReplacement(pem, "192.168.7.2");
        Assert.Throws<InvalidDataException>(() => CertificateFiles.ValidateReplacement(pem, "192.168.7.3"));
    }
}
