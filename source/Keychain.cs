using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

// The root key in the macOS login Keychain: a generic password, service mcred-root-key, account root-v1.
[SupportedOSPlatform("macos")]
internal static class Keychain
{
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const string CoreFoundationFramework = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int DuplicateItem = -25299;
    private const int ItemNotFound = -25300;

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainAddGenericPassword")]
    private static extern int AddGenericPassword(
        IntPtr keychain,
        uint serviceLength,
        byte[] service,
        uint accountLength,
        byte[] account,
        uint passwordLength,
        byte[] password,
        out IntPtr item);

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainFindGenericPassword")]
    private static extern int FindGenericPassword(
        IntPtr keychainOrArray,
        uint serviceLength,
        byte[] service,
        uint accountLength,
        byte[] account,
        out uint passwordLength,
        out IntPtr passwordData,
        out IntPtr item);

    [DllImport(SecurityFramework, EntryPoint = "SecKeychainItemFreeContent")]
    private static extern int FreeContent(IntPtr attributes, IntPtr data);

    [DllImport(CoreFoundationFramework, EntryPoint = "CFRelease")]
    private static extern void Release(IntPtr value);

    public static void Add(byte[] encoded)
    {
        byte[] service = Encoding.UTF8.GetBytes(RootKeyStore.Service);
        byte[] account = Encoding.UTF8.GetBytes(RootKeyStore.Account);
        IntPtr item = IntPtr.Zero;
        try
        {
            int status = AddGenericPassword(
                IntPtr.Zero,
                (uint)service.Length, service,
                (uint)account.Length, account,
                (uint)encoded.Length, encoded,
                out item);
            if (status == DuplicateItem)
                throw new InvalidOperationException("Root key already exists in Keychain; it was not changed.");
            if (status != 0)
                throw new InvalidOperationException($"Could not create root key in Keychain (OSStatus {status}).");
        }
        finally
        {
            if (item != IntPtr.Zero) Release(item);
        }
    }

    // The root key's bytes, or null when the Keychain has none.
    public static byte[]? TryFind()
    {
        byte[] service = Encoding.UTF8.GetBytes(RootKeyStore.Service);
        byte[] account = Encoding.UTF8.GetBytes(RootKeyStore.Account);
        IntPtr item = IntPtr.Zero;
        IntPtr data = IntPtr.Zero;
        try
        {
            int status = FindGenericPassword(
                IntPtr.Zero,
                (uint)service.Length, service,
                (uint)account.Length, account,
                out uint length, out data, out item);
            if (status == ItemNotFound)
                return null;
            if (status != 0)
                throw new InvalidOperationException($"Could not read root key from Keychain (OSStatus {status}).");
            if (data == IntPtr.Zero)
                throw new InvalidOperationException("The Keychain root key is empty.");
            byte[] encoded = new byte[length];
            Marshal.Copy(data, encoded, 0, encoded.Length);
            return encoded;
        }
        finally
        {
            if (data != IntPtr.Zero) FreeContent(IntPtr.Zero, data);
            if (item != IntPtr.Zero) Release(item);
        }
    }
}
