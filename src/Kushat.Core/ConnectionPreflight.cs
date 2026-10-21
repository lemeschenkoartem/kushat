using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Kushat.Core;

public sealed record PeerCertificate(byte[] Der, string Subject, string Issuer, string Sha256,
    DateTime ValidFromUtc, DateTime ValidUntilUtc)
{
    public static PeerCertificate From(X509Certificate2 cert) => new(cert.RawData, cert.Subject, cert.Issuer,
        cert.GetCertHashString(HashAlgorithmName.SHA256), cert.NotBefore.ToUniversalTime(), cert.NotAfter.ToUniversalTime());
}

public sealed record ConnectionReport(Uri Endpoint, bool PortOpen, bool Ready, string Summary,
    PeerCertificate? Certificate = null, string? Failure = null)
{
    public string Text => $"Адрес: {Endpoint.GetLeftPart(UriPartial.Authority)}\n" + Summary +
        (Certificate is null ? "" : $"\nСертификат: {Certificate.Subject}\n" +
        $"Срок UTC: {Certificate.ValidFromUtc:yyyy-MM-dd HH:mm} — {Certificate.ValidUntilUtc:yyyy-MM-dd HH:mm}\n" +
        $"SHA256: {Certificate.Sha256}") + (Failure is null ? "" : "\n" + Failure);
    public void EnsureReady() { if (!Ready) throw new ConnectionCheckException(this); }
}

public sealed class ConnectionCheckException(ConnectionReport report) : IOException(report.Text)
{
    public ConnectionReport Report { get; } = report;
}

public interface IConnectionProbe
{
    Task<ConnectionReport> CheckAsync(Uri endpoint, int timeoutSeconds = 10, CancellationToken cancellationToken = default);
}

public sealed class ConnectionPreflight : IConnectionProbe
{
    public async Task<ConnectionReport> CheckAsync(Uri endpoint, int timeoutSeconds = 10, CancellationToken cancellationToken = default)
    {
        if (endpoint.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(endpoint.UserInfo))
            throw new ArgumentException("Проверяется только HTTP/HTTPS-адрес без учётных данных.");
        if (timeoutSeconds is < 1 or > 300) throw new ArgumentException("Неверный таймаут проверки.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        using var socket = new TcpClient();
        var portOpen = false;
        PeerCertificate? peer = null;
        CertificateFailure? failure = null;
        try
        {
            await socket.ConnectAsync(endpoint.IdnHost, endpoint.Port, timeout.Token);
            portOpen = true;
            if (endpoint.Scheme == "http") return new(endpoint, true, true, "Порт доступен. HTTP: сертификат не используется.");
            using var tls = new SslStream(socket.GetStream(), false, (_, certificate, chain, errors) =>
            {
                if (certificate is not null)
                {
                    using var cert = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    peer = PeerCertificate.From(cert);
                    if (errors != SslPolicyErrors.None)
                        failure = new(errors, chain?.ChainStatus.Aggregate(X509ChainStatusFlags.NoError,
                            (flags, item) => flags | item.Status) ?? X509ChainStatusFlags.NoError,
                            cert.GetNameInfo(X509NameType.DnsName, false), peer.Sha256, peer.ValidFromUtc, peer.ValidUntilUtc);
                }
                return errors == SslPolicyErrors.None; // Observe failures; never accept an invalid peer.
            });
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = endpoint.IdnHost }, timeout.Token);
            var warning = peer is not null && peer.ValidUntilUtc < DateTime.UtcNow.AddDays(30)
                ? " Срок сертификата истекает менее чем через 30 дней." : "";
            return new(endpoint, true, true, "Порт доступен. HTTPS-сертификат действителен, имя и доверие проверены." + warning, peer);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(endpoint, portOpen, false, "TIMEOUT: Истёк таймаут проверки соединения.", peer); }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException)
        {
            var reason = failure is not null ? ConnectionDiagnostics.Transport(ex, failure) :
                ex is AuthenticationException ? "TLS_HANDSHAKE: Не удалось согласовать HTTPS. Проверьте протокол и порт." :
                ConnectionDiagnostics.Transport(ex, null);
            return new(endpoint, portOpen, false, portOpen ? "Порт доступен, проверка HTTPS не пройдена." : "Не удалось подключиться к порту.", peer, reason);
        }
    }
}

public sealed class ReferenceConnectionWorkflow(HttpClient http, IConnectionProbe? probe = null)
{
    public async Task<ReferenceCatalog> ConnectAsync(ReferenceConnection connection, string password,
        Action<ConnectionReport>? checkedServer = null, CancellationToken cancellationToken = default)
    {
        var endpoint = connection.GetEndpoint();
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Введите пароль сотрудника.");
        var report = await (probe ?? new ConnectionPreflight()).CheckAsync(endpoint,
            Math.Min(connection.TimeoutSeconds, 10), cancellationToken);
        checkedServer?.Invoke(report);
        report.EnsureReady();
        // Authentication is only attempted after the TCP/TLS preflight succeeds.
        return await new ReferenceServerClient(http).ConnectAsync(connection, password, cancellationToken);
    }
}
