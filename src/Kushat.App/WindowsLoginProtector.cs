using System.Security.Cryptography;
using Kushat.Core;

namespace Kushat.App;

public sealed class WindowsLoginProtector : ILoginProtector
{
    public byte[] Protect(byte[] plaintext, byte[] context) =>
        ProtectedData.Protect(plaintext, context, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] ciphertext, byte[] context) =>
        ProtectedData.Unprotect(ciphertext, context, DataProtectionScope.CurrentUser);
}
