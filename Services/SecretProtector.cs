using System.Security.Cryptography;
using System.Text;

namespace AiSecretary.Desktop.Services;

/// <summary>
/// Encrypts and decrypts local secrets using Windows DPAPI (CurrentUser scope).
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";

    public static string Protect(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        if (plaintext.StartsWith(Prefix, StringComparison.Ordinal)) return plaintext;

        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return "";
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;

        try
        {
            var cipherBytes = Convert.FromBase64String(stored[Prefix.Length..]);
            var plainBytes = ProtectedData.Unprotect(cipherBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return "";
        }
    }
}
