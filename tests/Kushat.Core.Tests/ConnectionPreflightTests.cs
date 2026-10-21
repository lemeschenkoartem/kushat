using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Kushat.CertificateTool;
using Kushat.Core;
using Xunit;

namespace Kushat.Core.Tests;

public class ConnectionPreflightTests
{
    [Fact]
    public async Task HttpPortIsCheckedWithoutSendingAnyAuthenticationOrHttpRequest()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var endpoint = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        var accept = listener.AcceptTcpClientAsync();
        var report = await new ConnectionPreflight().CheckAsync(endpoint);
        using var peer = await accept;
        var bytes = new byte[100];
        Assert.Equal(0, await peer.GetStream().ReadAsync(bytes).AsTask().WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(report.Ready); Assert.True(report.PortOpen); Assert.Null(report.Certificate);
    }

    [Fact]
    public async Task ClosedPortFailsBeforeAnyLoginCanBeSent()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var report = await new ConnectionPreflight().CheckAsync(new Uri($"https://127.0.0.1:{port}"));
        Assert.False(report.PortOpen); Assert.False(report.Ready);
        Assert.Contains("CONNECTION_REFUSED", report.Text);
    }

    [Fact]
    public async Task RealTlsProbeCapturesUntrustedCertificateAndRejectsItWithoutHttpTraffic()
    {
        var generated = LocalCertificate.Create(DateTimeOffset.UtcNow, "127.0.0.1");
        using var pem = X509Certificate2.CreateFromPem(generated.Pem, generated.Pem);
        using var cert = TlsTestCertificate.ForServer(pem);
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var read = Task.Run(async () =>
        {
            using var peer = await listener.AcceptTcpClientAsync(deadline.Token);
            using var tls = new SslStream(peer.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = cert }, deadline.Token);
                var bytes = new byte[100]; return await tls.ReadAsync(bytes, deadline.Token);
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException) { return 0; }
        });
        var endpoint = new Uri($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
        var report = await new ConnectionPreflight().CheckAsync(endpoint, cancellationToken: deadline.Token);
        Assert.True(report.PortOpen); Assert.False(report.Ready); Assert.NotNull(report.Certificate);
        Assert.Equal(generated.PublicCertificate, report.Certificate.Der);
        Assert.Contains("TLS_CERTIFICATE", report.Text); Assert.Equal(0, await read);
    }

    [Fact]
    public async Task FailedPreflightDoesNotIssueApiRequests()
    {
        using var http = new HttpClient(new Handler(_ => throw new Exception("Credentials must never be sent")));
        var probe = new Probe(false);
        var connection = new ReferenceConnection { ServerAddress = "https://example.test:8010", UserName = "labels" };
        var error = await Assert.ThrowsAsync<ConnectionCheckException>(() => new ReferenceConnectionWorkflow(http, probe)
            .ConnectAsync(connection, "not-for-diagnostics"));
        Assert.DoesNotContain("not-for-diagnostics", error.Message); Assert.True(probe.Called);
    }

    [Fact]
    public async Task SuccessfulPreflightPrecedesBothAuthenticatedApiReads()
    {
        var probe = new Probe(true); var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Assert.True(probe.Called); Assert.NotNull(request.Headers.Authorization); calls++;
            var item = calls == 1 ? "<Item Ident=\"3\" Name=\"Основной\"/>" : "<Item Ident=\"1\" Name=\"Товар\" PriceMode=\"pmPerPiece\" PRICETYPES-3=\"55\"/>";
            return new(HttpStatusCode.OK) { Content = new StringContent("<RK7QueryResult Status=\"Ok\"><RK7Reference><Items>" + item + "</Items></RK7Reference></RK7QueryResult>") };
        }));
        await new ReferenceConnectionWorkflow(http, probe).ConnectAsync(new() { ServerAddress = "https://example.test:8010", UserName = "labels" }, "password");
        Assert.Equal(2, calls);
    }

    [Fact]
    public void PreflightAddressDoesNotRequireEmployeeOrPassword()
    {
        Assert.Equal("example.test", new ReferenceConnection { ServerAddress = "https://example.test:8010" }.GetServerEndpoint().Host);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response(request));
    }
    private sealed class Probe(bool ready) : IConnectionProbe
    {
        public bool Called { get; private set; }
        public Task<ConnectionReport> CheckAsync(Uri endpoint, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
        { Called = true; return Task.FromResult(new ConnectionReport(endpoint, ready, ready, ready ? "Ready" : "Not ready")); }
    }
}
