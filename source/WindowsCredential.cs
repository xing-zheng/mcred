using System.Runtime.InteropServices;
using System.Runtime.Versioning;

// The root key in the Windows Credential Manager: a generic credential, target mcred-root-key/root-v1, persisted for the
// local machine under the current user (the Keychain's counterpart on the Windows seat).
[SupportedOSPlatform("windows")]
internal static class WindowsCredential
{
    private const string Target = RootKeyStore.Service + "/" + RootKeyStore.Account;
    private const uint GenericType = 1;
    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

    public static void Add(byte[] encoded)
    {
        // CredWrite replaces an existing credential, so the refusal to replace a root key is checked first.
        if (CredRead(Target, GenericType, 0, out IntPtr existing))
        {
            CredFree(existing);
            throw new InvalidOperationException("Root key already exists in Windows Credential Manager; it was not changed.");
        }
        IntPtr blob = Marshal.AllocHGlobal(encoded.Length);
        try
        {
            Marshal.Copy(encoded, 0, blob, encoded.Length);
            var credential = new Credential
            {
                Type = GenericType,
                TargetName = Target,
                CredentialBlobSize = (uint)encoded.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                UserName = RootKeyStore.Account
            };
            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException(
                    $"Could not create root key in Windows Credential Manager (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            for (int i = 0; i < encoded.Length; i++) Marshal.WriteByte(blob, i, 0);
            Marshal.FreeHGlobal(blob);
        }
    }

    // The root key's bytes, or null when the Credential Manager has none.
    public static byte[]? TryFind()
    {
        if (!CredRead(Target, GenericType, 0, out IntPtr pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
                return null;
            throw new InvalidOperationException($"Could not read root key from Windows Credential Manager (error {error}).");
        }
        try
        {
            Credential credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero)
                throw new InvalidOperationException("The Windows Credential Manager root key is empty.");
            byte[] encoded = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, encoded, 0, encoded.Length);
            return encoded;
        }
        finally
        {
            CredFree(pointer);
        }
    }
}
