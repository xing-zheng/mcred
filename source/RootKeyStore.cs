using System.Security.Cryptography;
using System.Text;

// Where a seat's root key rests, as canonical Base64 (44 ASCII bytes): the macOS login Keychain, or the Windows
// Credential Manager. The same password gives the same key on both (RootKeyDerivation), so a Mac and a Windows seat
// share ciphertext.
internal interface IRootKeyStore
{
    // The stored Base64 bytes, or null when this seat has no root key yet.
    byte[]? TryRead();

    // Refuses to replace an existing key.
    void Add(byte[] encoded);
}

internal sealed class SystemRootKeyStore : IRootKeyStore
{
    public byte[]? TryRead() =>
        OperatingSystem.IsMacOS() ? Keychain.TryFind()
        : OperatingSystem.IsWindows() ? WindowsCredential.TryFind()
        : throw RootKeyStore.Unsupported();

    public void Add(byte[] encoded)
    {
        if (OperatingSystem.IsMacOS())
            Keychain.Add(encoded);
        else if (OperatingSystem.IsWindows())
            WindowsCredential.Add(encoded);
        else
            throw RootKeyStore.Unsupported();
    }
}

internal static class RootKeyStore
{
    public const string Service = "mcred-root-key";
    public const string Account = "root-v1";

    // The test assembly swaps in an in-memory store; no command accepts a raw root key.
    internal static IRootKeyStore Current { get; set; } = new SystemRootKeyStore();

    public static void Initialize(string rootKey)
    {
        byte[] decoded = DecodeRootKey(rootKey);
        CryptographicOperations.ZeroMemory(decoded);
        byte[] encoded = Encoding.ASCII.GetBytes(rootKey);
        try
        {
            Current.Add(encoded);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    public static bool Exists()
    {
        byte[]? encoded = Current.TryRead();
        if (encoded is null) return false;
        CryptographicOperations.ZeroMemory(encoded);
        return true;
    }

    public static byte[] ReadRootKey() =>
        TryReadRootKey() ?? throw new InvalidOperationException(
            "Root key not found in the macOS Keychain or the Windows Credential Manager. Run 'mcred init' with a password " +
            "already in use, 'mcred init --new' with a first password, or 'mcred seat join <name>' with a one-off root key.");

    public static byte[]? TryReadRootKey()
    {
        byte[]? encoded = Current.TryRead();
        if (encoded is null) return null;
        try
        {
            if (encoded.Length != 44)
                throw new InvalidOperationException("The stored root key is not canonical Base64 for 32 bytes.");
            return DecodeRootKey(Encoding.ASCII.GetString(encoded));
        }
        catch (ArgumentException error)
        {
            throw new InvalidOperationException("The stored root key is not canonical Base64 for 32 bytes.", error);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }

    internal static byte[] DecodeRootKey(string encoded)
    {
        const string error = "Root key must be canonical Base64 encoding of exactly 32 bytes (44 characters ending in '=').";
        byte[] root;
        try
        {
            root = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            throw new ArgumentException(error);
        }
        if (root.Length != 32 || Convert.ToBase64String(root) != encoded)
        {
            CryptographicOperations.ZeroMemory(root);
            throw new ArgumentException(error);
        }
        return root;
    }

    internal static PlatformNotSupportedException Unsupported() =>
        new("This version stores its root key in the macOS Keychain or the Windows Credential Manager.");
}
