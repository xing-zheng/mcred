using System.Security.Cryptography;
using System.Text;

// A store's values. Format 1: v2 under the seat's root key (mcred 1.0.0's, unchanged). Formats 2 and 3: v3 under the
// repository key, whose earlier keys (after a rotation) still open the values written before it.
internal interface IValueCipher
{
    string Encrypt(byte[] plaintext, string name);
    byte[] Decrypt(string value, string name);
}

internal static class Ciphers
{
    public static IValueCipher Open(Repository repository)
    {
        RepositoryMetadata metadata = repository.RequireStore();
        return metadata.Format == 1 ? new RootKeyCipher() : Seats.OpenCipher(repository, metadata);
    }
}

internal sealed class RootKeyCipher : IValueCipher
{
    public string Encrypt(byte[] plaintext, string name)
    {
        byte[] root = RootKeyStore.ReadRootKey();
        try { return Encryption.EncryptV2(root, plaintext, name); }
        finally { CryptographicOperations.ZeroMemory(root); }
    }

    public byte[] Decrypt(string value, string name)
    {
        byte[] root = RootKeyStore.ReadRootKey();
        try { return Encryption.DecryptV2(root, value, name); }
        finally { CryptographicOperations.ZeroMemory(root); }
    }
}

internal sealed class RepositoryKeyCipher(byte[] currentKey, string currentKeyId, IReadOnlyDictionary<string, byte[]> keys)
    : IValueCipher
{
    public string CurrentKeyId => currentKeyId;

    public string Encrypt(byte[] plaintext, string name) => Crypto.SealValue(currentKey, currentKeyId, plaintext, name);

    public byte[] Decrypt(string value, string name)
    {
        if (value.StartsWith("v2.", StringComparison.Ordinal))
            throw new InvalidOperationException($"'{name}' is still a v2 value under a root key: run 'mcred migrate <seat>'.");
        string keyId = Crypto.ValueKeyId(value);
        if (!keys.TryGetValue(keyId, out byte[]? key))
            throw new InvalidDataException(
                $"'{name}' is encrypted under the repository key {keyId}, which this store does not hold (its key is {currentKeyId}).");
        return Crypto.OpenValue(key, value, name);
    }
}

internal static class Encryption
{
    private const string Version = "v2";
    private const int SaltLength = 16;
    private const int KdfIterations = 600_000;
    private const int NonceLength = 12;
    private const int TagLength = 16;

    public static string EncryptV2(byte[] root, byte[] plaintext, string associatedName)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(root, salt, KdfIterations, HashAlgorithmName.SHA256, 32);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(associatedName));
            return string.Join('.', Version, Convert.ToBase64String(salt),
                Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static byte[] DecryptV2(byte[] root, string value, string associatedName)
    {
        string[] parts = value.Split('.');
        if (parts.Length != 5 || parts[0] != Version)
            throw new InvalidDataException("Unsupported or malformed encrypted value.");
        byte[] salt = Convert.FromBase64String(parts[1]);
        byte[] nonce = Convert.FromBase64String(parts[2]);
        byte[] ciphertext = Convert.FromBase64String(parts[3]);
        byte[] tag = Convert.FromBase64String(parts[4]);
        if (salt.Length != SaltLength || nonce.Length != NonceLength || tag.Length != TagLength)
            throw new InvalidDataException("Malformed encrypted value.");
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(root, salt, KdfIterations, HashAlgorithmName.SHA256, 32);
        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            try
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(associatedName));
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new InvalidDataException("Encrypted value authentication failed.");
            }
            return plaintext;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] AssociatedData(string name) => Encoding.UTF8.GetBytes($"{Version}:{name}");
}
