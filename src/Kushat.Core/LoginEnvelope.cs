using System.Security.Cryptography;
using System.Text;

namespace Kushat.Core;

public interface ILoginProtector
{
    byte[] Protect(byte[] plaintext, byte[] context);
    byte[] Unprotect(byte[] ciphertext, byte[] context);
}

// Account binding prevents a saved password being reused for another server or employee.
public sealed record LoginEnvelope(string AccountKey, string Ciphertext)
{
    public static LoginEnvelope Create(ReferenceConnection connection, string password, ILoginProtector protector)
    {
        if (string.IsNullOrEmpty(password)) throw new ArgumentException("Пароль не задан.");
        var context = Context(connection);
        var plaintext = Encoding.UTF8.GetBytes(password);
        try { return new(Convert.ToHexString(context), Convert.ToBase64String(protector.Protect(plaintext, context))); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public string Open(ReferenceConnection connection, ILoginProtector protector)
    {
        var context = Context(connection);
        if (AccountKey != Convert.ToHexString(context))
            throw new CryptographicException("Сохранённый вход относится к другому серверу или сотруднику.");
        var plaintext = protector.Unprotect(Convert.FromBase64String(Ciphertext), context);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static string Key(ReferenceConnection connection) => Convert.ToHexString(Context(connection));
    private static byte[] Context(ReferenceConnection connection) => SHA256.HashData(Encoding.UTF8.GetBytes(
        "Kushat/Login/v1\n" + connection.GetEndpoint().AbsoluteUri + "\n" + connection.UserName.Trim()));
}
