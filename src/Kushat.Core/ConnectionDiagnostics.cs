using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace Kushat.Core;

public sealed class ReferenceConnectionException : IOException
{
    public ReferenceConnectionException(Uri endpoint, Exception cause, CertificateFailure? certificate)
        : base(ConnectionDiagnostics.Transport(cause, certificate) + "\nАдрес: " + endpoint.GetLeftPart(UriPartial.Authority), cause) { }
}

public static class ConnectionDiagnostics
{
    // Never display raw transport messages, request headers, bodies or response bodies.
    public static string Describe(Exception error) => error switch
    {
        ReferenceConnectionException connection => connection.Message,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized } =>
            "HTTP_401: Сервер отклонил авторизацию. Проверьте учётную запись HTTP/XML и пароль.",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden } =>
            "HTTP_403: Сервер запретил запрос. Проверьте права HTTP/XML-доступа.",
        HttpRequestException { StatusCode: { } code } =>
            $"HTTP_{(int)code}: Сервер вернул ошибку. Проверьте адрес XML-интерфейса и настройки сервера.",
        HttpRequestException or IOException => Transport(error, null),
        FormatException or ArgumentException => error.Message,
        _ => "Не удалось выполнить запрос к серверу."
    };

    internal static string Transport(Exception error, CertificateFailure? certificate)
    {
        if (certificate is not null)
        {
            var lines = new List<string> { "TLS_CERTIFICATE: Windows отклонила сертификат сервера.",
                "Проверка: " + certificate.Errors, "Цепочка: " + certificate.ChainErrors };
            if (certificate.Errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))
                lines.Add("Имя в адресе не совпадает с сертификатом. Проверьте DNS-имя сервера и имена в сертификате.");
            if ((certificate.ChainErrors & (X509ChainStatusFlags.UntrustedRoot | X509ChainStatusFlags.PartialChain)) != 0)
                lines.Add("Windows не доверяет цепочке. Проверьте сертификат и настройте доверие к проверенному центру сертификации.");
            if (certificate.ChainErrors.HasFlag(X509ChainStatusFlags.NotTimeValid))
                lines.Add("Проверьте срок действия сертификата, дату и время компьютера.");
            if (certificate.DnsName.Length > 0) lines.Add("Имя сертификата: " + OneLine(certificate.DnsName));
            if (certificate.ValidFromUtc is { } start && certificate.ValidUntilUtc is { } end)
                lines.Add($"Срок действия (UTC): {start:yyyy-MM-dd HH:mm} — {end:yyyy-MM-dd HH:mm}");
            if (certificate.Sha256.Length > 0) lines.Add("SHA256 сертификата: " + certificate.Sha256);
            return string.Join("\n", lines);
        }
        for (Exception? inner = error; inner is not null; inner = inner.InnerException)
        {
            if (inner is not SocketException socket) continue;
            var code = $" ({socket.SocketErrorCode}, {socket.NativeErrorCode})";
            return socket.SocketErrorCode switch
            {
                SocketError.ConnectionRefused => "CONNECTION_REFUSED: Соединение отклонено. Проверьте, слушает ли сервер выбранный адрес и порт." + code,
                SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "DNS_FAILURE: Не удалось определить адрес сервера по имени." + code,
                SocketError.NetworkUnreachable or SocketError.HostUnreachable => "NETWORK_UNREACHABLE: Сервер недоступен по сети." + code,
                SocketError.TimedOut => "TIMEOUT: Сервер не ответил на сетевое подключение." + code,
                SocketError.ConnectionReset or SocketError.ConnectionAborted => "CONNECTION_RESET: Соединение оборвано. Проверьте соответствие HTTP/HTTPS и порт сервера." + code,
                _ => "SOCKET_ERROR: Ошибка сетевого подключения." + code
            };
        }
        if (error is HttpRequestException http)
            return http.HttpRequestError switch
            {
                HttpRequestError.SecureConnectionError => "TLS_HANDSHAKE: Не удалось установить защищённое соединение. Возможны ошибка сертификата или несовместимые параметры TLS. Подробности сертификата сервер не предоставил.",
                HttpRequestError.NameResolutionError => "DNS_FAILURE: Не удалось определить адрес сервера по имени.",
                HttpRequestError.ProxyTunnelError => "PROXY_ERROR: Не удалось установить соединение через прокси. Проверьте настройки доступа к локальному серверу.",
                HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded or HttpRequestError.HttpProtocolError =>
                    "HTTP_RESPONSE: Сервер оборвал соединение или вернул некорректный HTTP-ответ. Проверьте HTTP/HTTPS и назначение порта.",
                _ => "HTTP_CONNECTION: Не удалось выполнить сетевой запрос. Проверьте адрес, порт и доступность сервера. Код: " + http.HttpRequestError
            };
        return "IO_CONNECTION: Соединение оборвано при чтении ответа сервера. Проверьте протокол и журнал сервера.";
    }

    private static string OneLine(string value) => new(value.Where(c => !char.IsControl(c)).Take(200).ToArray());
}
