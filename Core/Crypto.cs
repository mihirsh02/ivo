using System.Security.Cryptography;
using System.Text;

namespace Ivo;

// The building blocks of the app lock, encryption at rest and encrypted backups.
// Seal / Open: AES-256-GCM, "nonce | tag | ciphertext" as base64. Wrap / Unwrap: a key sealed with a key derived from a
// secret (PIN, password, recovery code). Hash / Matches: a salted PBKDF2 check of a secret.
public static class Crypto
{
    const int Rounds = 200_000;

    public static byte[] KeyFrom(string secret, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(secret, salt, Rounds, HashAlgorithmName.SHA256, 32);

    public static string Seal(byte[] key, byte[] plain) => Convert.ToBase64String(SealBytes(key, plain));
    public static byte[] Open(byte[] key, string sealedText) => OpenBytes(key, Convert.FromBase64String(sealedText));

    public static byte[] SealBytes(byte[] key, byte[] plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12); var tag = new byte[16]; var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, cipher, tag);
        return [.. nonce, .. tag, .. cipher];
    }
    public static byte[] OpenBytes(byte[] key, byte[] all)
    {
        var plain = new byte[all.Length - 28];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(all[..12], all[28..], all[12..28], plain);
        return plain;
    }

    public static string Wrap(byte[] key, string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(salt) + ":" + Seal(KeyFrom(secret, salt), key);
    }
    public static byte[]? Unwrap(string wrapped, string secret)
    {
        var p = wrapped.Split(':');
        if (p.Length != 2) return null;
        try { return Open(KeyFrom(secret, Convert.FromBase64String(p[0])), p[1]); }
        catch (Exception e) when (e is CryptographicException or FormatException) { return null; }
    }

    public static string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return Convert.ToBase64String(salt) + ":" + Convert.ToBase64String(KeyFrom(secret, salt));
    }
    public static bool Matches(string hash, string secret)
    {
        var p = hash.Split(':');
        return p.Length == 2 && CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(p[1]), KeyFrom(secret, Convert.FromBase64String(p[0])));
    }

    // a file sealed with a password (encrypted backups): "IVOBAK1" | salt | sealed
    static readonly byte[] BackupTag = Encoding.ASCII.GetBytes("IVOBAK1");
    public static byte[] SealWithPassword(byte[] plain, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return [.. BackupTag, .. salt, .. SealBytes(KeyFrom(password, salt), plain)];
    }
    public static bool IsPasswordSealed(byte[] data) => data.Length > 23 && data.AsSpan(0, 7).SequenceEqual(BackupTag);
    public static byte[]? OpenWithPassword(byte[] data, string password)
    {
        try { return OpenBytes(KeyFrom(password, data[7..23]), data[23..]); }
        catch (CryptographicException) { return null; }
    }
}
