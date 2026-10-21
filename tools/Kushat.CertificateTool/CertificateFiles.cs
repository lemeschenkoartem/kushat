using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

namespace Kushat.CertificateTool;

// Platform-independent, guarded file changes; Windows process/ACL checks are supplied by the caller.
public static class CertificateFiles
{
    public const string Backup = "mylocal.before.pem";
    public const string Replacement = "mylocal.new.pem";
    public const string ManifestName = "preparation.json";
    public sealed record Preparation(int Version, string ServerDirectory, string KeysDirectory,
        string Host, string DefaultFileHash, string? OriginalFileHash, string NewFileHash);

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    public static Preparation Read(string directory)
    {
        var state = JsonSerializer.Deserialize<Preparation>(File.ReadAllText(Path.Combine(directory, ManifestName)))
            ?? throw new InvalidDataException("Не найдены данные подготовки.");
        if (state.Version != 2 || string.IsNullOrWhiteSpace(state.ServerDirectory) ||
            string.IsNullOrWhiteSpace(state.KeysDirectory) || string.IsNullOrWhiteSpace(state.Host))
            throw new InvalidDataException("Нужна папка подготовки версии 2. Подготовка старой утилиты не подходит.");
        if (!string.Equals(Path.GetFullPath(Path.GetDirectoryName(Path.GetFullPath(directory))!),
                Path.GetFullPath(state.KeysDirectory), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Папка подготовки перемещена. Укажите её исходное расположение.");
        return state;
    }

    public static void ValidateReplacement(byte[] bytes, string host)
    {
        var pem = Encoding.ASCII.GetString(bytes);
        using var cert = X509Certificate2.CreateFromPem(pem, pem);
        if (!cert.HasPrivateKey || cert.NotBefore.ToUniversalTime() > DateTime.UtcNow ||
            cert.NotAfter.ToUniversalTime() <= DateTime.UtcNow ||
            !cert.MatchesHostname(host, allowWildcards: false, allowCommonName: false))
            throw new InvalidDataException("Ключ, срок или SAN нового сертификата не прошёл проверку.");
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(cert);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // Self-signed; no CRL service.
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
        if (!chain.Build(cert)) throw new InvalidDataException("Не прошла проверка нового серверного сертификата.");
    }

    public static void Apply(string directory, bool rollback, Action<Preparation> requireStopped,
        Func<string, string, FileStream> createNewFile)
    {
        var state = Read(directory);
        var defaultFile = Path.Combine(state.KeysDirectory, "default.pem");
        if (!rollback && Hash(File.ReadAllBytes(defaultFile)) != state.DefaultFileHash)
            throw new InvalidDataException("default.pem изменился после подготовки. Операция отменена.");
        var target = Path.Combine(state.KeysDirectory, "mylocal.pem");
        byte[]? original = state.OriginalFileHash is null ? null : File.ReadAllBytes(Path.Combine(directory, Backup));
        if (original is not null && Hash(original) != state.OriginalFileHash)
            throw new InvalidDataException("Резервная копия изменена. Операция отменена.");
        var replacement = File.ReadAllBytes(Path.Combine(directory, Replacement));
        if (Hash(replacement) != state.NewFileHash)
            throw new InvalidDataException("Новый PEM изменён. Операция отменена.");
        if (!rollback) ValidateReplacement(replacement, state.Host);
        var expected = rollback ? state.NewFileHash : state.OriginalFileHash;
        requireStopped(state); // Must fail before any active-file mutation.
        if (expected is null)
        {
            // CreateNew refuses to overwrite a custom certificate that appeared after preparation.
            using var stream = createNewFile(target, defaultFile);
            try
            {
                Write(stream, replacement);
                stream.Position = 0;
                if (Hash(ReadAll(stream)) != state.NewFileHash) throw new IOException("Ошибка проверки записи.");
            }
            catch
            {
                stream.Dispose();
                File.Delete(target); // Only the file just created by this operation.
                throw;
            }
            return;
        }
        using (var stream = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var current = ReadAll(stream);
            if (Hash(current) != expected)
                throw new InvalidDataException("mylocal.pem изменился. Операция отменена.");
            if (rollback && original is null)
            {
                // Close before deleting on Windows; stopped server and hash guard protect the rollback.
                stream.Dispose();
                File.Delete(target);
                return;
            }
            var requested = rollback ? original! : replacement;
            try
            {
                Write(stream, requested); // Existing owner/DACL are preserved.
                stream.Position = 0;
                if (Hash(ReadAll(stream)) != Hash(requested)) throw new IOException("Ошибка проверки записи.");
            }
            catch
            {
                try { Write(stream, current); }
                catch { throw new IOException("Не удалось восстановить mylocal.pem. Используйте резервную копию при остановленном RefSrv: " + directory); }
                throw;
            }
        }
    }

    static byte[] ReadAll(FileStream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    static void Write(FileStream stream, byte[] bytes)
    {
        stream.Position = 0;
        stream.SetLength(0);
        stream.Write(bytes);
        stream.Flush(true);
    }
}
