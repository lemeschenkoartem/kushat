using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Kushat.Core;
using Xunit;

namespace Kushat.Core.Tests;

public class ConnectionDiagnosticsTests
{
    [Theory]
    [InlineData(HttpRequestError.SecureConnectionError, "TLS_HANDSHAKE")]
    [InlineData(HttpRequestError.NameResolutionError, "DNS_FAILURE")]
    [InlineData(HttpRequestError.ProxyTunnelError, "PROXY_ERROR")]
    [InlineData(HttpRequestError.ResponseEnded, "HTTP_RESPONSE")]
    [InlineData(HttpRequestError.InvalidResponse, "HTTP_RESPONSE")]
    public async Task ClassifiesTransportFailureWithoutLeakingRawException(HttpRequestError kind, string code)
    {
        const string secret = "do-not-output-password-or-response";
        using var http = new HttpClient(new FailingHandler(new HttpRequestException(kind, secret)));
        var error = await Assert.ThrowsAsync<ReferenceConnectionException>(() =>
            new ReferenceServerClient(http).ConnectAsync(Connection(), secret));
        var display = ConnectionDiagnostics.Describe(error);
        Assert.Contains(code, display);
        Assert.Contains("https://reference.test:8086", display);
        Assert.DoesNotContain(secret, display);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "HTTP_401")]
    [InlineData(HttpStatusCode.Forbidden, "HTTP_403")]
    [InlineData(HttpStatusCode.NotFound, "HTTP_404")]
    public void DistinguishesAuthenticationAndHttpErrors(HttpStatusCode status, string code)
    {
        var display = ConnectionDiagnostics.Describe(new HttpRequestException("private", null, status));
        Assert.Contains(code, display); Assert.DoesNotContain("private", display);
    }

    [Fact]
    public void ReportsCertificateNameExpiryAndChainWithoutRawException()
    {
        var certificate = new CertificateFailure(SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors,
            X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.NotTimeValid,
            "server.local\n", "PUBLIC-SHA256", new DateTime(2020, 1, 1), new DateTime(2021, 1, 1));
        var error = new ReferenceConnectionException(new Uri("https://127.0.0.1:8086"),
            new HttpRequestException("private"), certificate);
        var display = ConnectionDiagnostics.Describe(error);
        Assert.Contains("TLS_CERTIFICATE", display);
        Assert.Contains("RemoteCertificateNameMismatch", display);
        Assert.Contains("UntrustedRoot", display);
        Assert.Contains("NotTimeValid", display);
        Assert.Contains("2020-01-01", display);
        Assert.Contains("PUBLIC-SHA256", display);
        Assert.DoesNotContain("private", display);
    }

    [Fact]
    public void SanitizesUnknownIoFailure() =>
        Assert.DoesNotContain("private", ConnectionDiagnostics.Describe(new IOException("private")));

    [Fact]
    public async Task ReportsRealConnectionRefusalSeparatelyFromTlsAndAuthentication()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        using var http = ReferenceHttpClient.Create();
        var connection = new ReferenceConnection { ServerAddress = $"http://127.0.0.1:{port}", UserName = "employee", TimeoutSeconds = 5 };
        var error = await Assert.ThrowsAsync<ReferenceConnectionException>(() =>
            new ReferenceServerClient(http).ConnectAsync(connection, "never-display-password"));
        Assert.Contains("CONNECTION_REFUSED", ConnectionDiagnostics.Describe(error));
        Assert.DoesNotContain("never-display-password", error.Message);
    }

    [Fact]
    public async Task RealTlsConnectionRejectsUntrustedCertificateAndSendsNoHttpCredentials()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var peerTask = ReadTlsPeer(listener, certificate, timeout.Token);
        using var http = ReferenceHttpClient.Create();
        try
        {
            var connection = new ReferenceConnection { ServerAddress = $"https://127.0.0.1:{port}", UserName = "employee", TimeoutSeconds = 10 };
            var error = await Assert.ThrowsAsync<ReferenceConnectionException>(() =>
                new ReferenceServerClient(http).ConnectAsync(connection, "never-send-this-password"));
            Assert.Contains("TLS_CERTIFICATE", error.Message);
            Assert.Contains("UntrustedRoot", error.Message);
            Assert.Contains(certificate.GetCertHashString(HashAlgorithmName.SHA256), error.Message);
            Assert.DoesNotContain("never-send-this-password", error.Message);
            Assert.False(await peerTask.WaitAsync(timeout.Token));
        }
        finally
        {
            await timeout.CancelAsync(); listener.Stop();
            await peerTask;
        }
    }

    private static async Task<bool> ReadTlsPeer(TcpListener listener, X509Certificate2 certificate, CancellationToken ct)
    {
        try
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            using var ssl = new SslStream(client.GetStream());
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, ct);
            var bytes = new byte[4096];
            return await ssl.ReadAsync(bytes, ct) > 0;
        }
        catch (Exception ex) when (ex is AuthenticationException or IOException or OperationCanceledException or SocketException)
        { return false; }
    }

    private static ReferenceConnection Connection() => new() { ServerAddress = "https://reference.test:8086", UserName = "employee" };

    private sealed class FailingHandler(Exception error) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(error);
    }
}
