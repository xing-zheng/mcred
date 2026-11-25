using System.Security.Cryptography;

// The keys of a store in formats 2 and 3 (README.md, "Formats"): a random repository key per repository, opened through slots.
// Every key here is random or already derived from a password, so HKDF-SHA256 makes the AES keys; no PBKDF2.
internal static class Crypto
{
    public const string RepositoryKeyLabel = "mcred repository key id";
    public const string SeatKeyLabel = "mcred seat key id";
    public const string OneOffKeyLabel = "mcred one-off key id";
    public const string SlotInfo = "mcred slot v1";
    public const string EarlierKeyInfo = "mcred earlier key v1";
    public const string ValueInfo = "mcred value v3";

    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    // The first 8 bytes of HMAC-SHA256(key, label), in hex: names a key without telling anything about it.
    public static string KeyId(byte[] key, string label) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Config.Utf8.GetBytes(label)), 0, 8).ToLowerInvariant();

    // <prefix>.<salt>.<nonce>.<ciphertext>.<tag>: AES-256-GCM under HKDF-SHA256(ikm, salt, info), the associated data
    // binding everything the opener relies on.
    public static string Seal(string prefix, byte[] ikm, string info, byte[] plaintext, string associatedData)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        byte[] key = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, Config.Utf8.GetBytes(info));
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagLength];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, Config.Utf8.GetBytes(associatedData));
            return string.Join('.', prefix, Convert.ToBase64String(salt), Convert.ToBase64String(nonce),
                Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static byte[] Open(string prefix, byte[] ikm, string info, string value, string associatedData, string what)
    {
        string[] parts = value.Split('.');
        if (parts.Length != 5 || parts[0] != prefix)
            throw new InvalidDataException($"Malformed {what}.");
        return OpenParts(parts[1], parts[2], parts[3], parts[4], ikm, info, associatedData, what);
    }

    // A value: v3.<repository key id>.<salt>.<nonce>.<ciphertext>.<tag>, associated data v3:<key id>:<name>.
    public static string SealValue(byte[] repositoryKey, string keyId, byte[] plaintext, string name)
    {
        string sealedValue = Seal("v3", repositoryKey, ValueInfo, plaintext, $"v3:{keyId}:{name}");
        return "v3." + keyId + sealedValue[2..];
    }

    public static string ValueKeyId(string value)
    {
        string[] parts = value.Split('.');
        if (parts.Length != 6 || parts[0] != "v3")
            throw new InvalidDataException("Malformed v3 value.");
        return parts[1];
    }

    public static byte[] OpenValue(byte[] repositoryKey, string value, string name)
    {
        string[] parts = value.Split('.');
        if (parts.Length != 6 || parts[0] != "v3")
            throw new InvalidDataException("Malformed v3 value.");
        return OpenParts(parts[2], parts[3], parts[4], parts[5], repositoryKey, ValueInfo, $"v3:{parts[1]}:{name}",
            "encrypted value");
    }

    private static byte[] OpenParts(string saltText, string nonceText, string ciphertextText, string tagText, byte[] ikm,
        string info, string associatedData, string what)
    {
        byte[] salt = Convert.FromBase64String(saltText);
        byte[] nonce = Convert.FromBase64String(nonceText);
        byte[] ciphertext = Convert.FromBase64String(ciphertextText);
        byte[] tag = Convert.FromBase64String(tagText);
        if (salt.Length != SaltLength || nonce.Length != NonceLength || tag.Length != TagLength)
            throw new InvalidDataException($"Malformed {what}.");
        byte[] key = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, Config.Utf8.GetBytes(info));
        byte[] plaintext = new byte[ciphertext.Length];
        try
        {
            using var aes = new AesGcm(key, TagLength);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Config.Utf8.GetBytes(associatedData));
            return plaintext;
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException($"The {what} did not open (wrong key, or a changed name, kind, expiry or id).");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // A one-off root key on the screen: RFC 4648 base32, no padding, in groups of five.
    public static string ToCode(byte[] bytes)
    {
        var text = new System.Text.StringBuilder();
        int buffer = 0, bits = 0;
        foreach (byte b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) text.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);
        return string.Join('-', text.ToString().Chunk(5).Select(group => new string(group)));
    }

    // Accepted with or without dashes and spaces, in either case.
    public static byte[] FromCode(string code, int length)
    {
        string text = new(code.Where(c => c is not ('-' or ' ' or '\t' or '\r' or '\n')).Select(char.ToUpperInvariant).ToArray());
        var bytes = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (char c in text)
        {
            int value = Base32Alphabet.IndexOf(c);
            if (value < 0) throw new ArgumentException("The one-off root key holds a character outside its alphabet.");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        if (bytes.Count != length)
            throw new ArgumentException($"The one-off root key must be {length * 8} bits ({(length * 8 + 4) / 5} characters).");
        return bytes.ToArray();
    }
}
