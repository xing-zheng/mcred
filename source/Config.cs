using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

internal static class Config
{
    // The one variable mcred reads: the clear SSH tree, which several repositories may share (~/pem).
    public const string SshClearVariable = "MCRED_DECRYPTED_SSH_KEYS_PATH";
    private const string RetiredSshClearVariable = "MCRED_DECRYPTED_SSH_KEYS";

    private static readonly UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly UnixFileMode PrivateDirectory = PrivateFile | UnixFileMode.UserExecute;

    public static readonly UTF8Encoding Utf8 = new(false, true);

    // Indented with LF on every OS, so a write on the Windows seat no longer turns a store into CRLF.
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, NewLine = "\n" };

    public static string Version =>
        typeof(Config).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown";

    public static string? SshClearRoot()
    {
        string? value = Environment.GetEnvironmentVariable(SshClearVariable);
        return string.IsNullOrWhiteSpace(value) ? null : ExpandPath(value);
    }

    public static string RequiredSshClearRoot()
    {
        string? root = SshClearRoot();
        if (root is not null) return root;
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(RetiredSshClearVariable)))
            throw new InvalidOperationException($"{RetiredSshClearVariable} is no longer read: set {SshClearVariable} to the same path.");
        throw new InvalidOperationException($"{SshClearVariable} must be set to a path (the clear SSH keys, e.g. ~/pem).");
    }

    public static string ExpandPath(string value)
    {
        if (value == "~" || value.StartsWith("~/", StringComparison.Ordinal))
            value = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[1..].TrimStart('/'));
        return Path.GetFullPath(value);
    }

    // Both seats' default file systems (APFS, NTFS) ignore case, so the comparison does too.
    public static bool IsSameOrInside(string path, string root)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string container = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return string.Equals(full, container, StringComparison.OrdinalIgnoreCase) ||
               full.StartsWith(container + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static void EnsurePrivateDirectory(string path)
    {
        RequireSupportedOS();
        string full = Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Directory is a symbolic link: {full}");
            return;
        }
        string? parent = Path.GetDirectoryName(full);
        if (parent is not null && parent != full)
            EnsurePrivateDirectory(parent);
        Directory.CreateDirectory(full);
        if (OperatingSystem.IsWindows())
            new DirectoryInfo(full).SetAccessControl(OwnerOnlyDirectory());
        else
            File.SetUnixFileMode(full, PrivateDirectory);
    }

    public static void AtomicWrite(string path, byte[] content)
    {
        RequireSupportedOS();
        string parent = Path.GetDirectoryName(path)!;
        EnsurePrivateDirectory(parent);
        string temporary = Path.Combine(parent, ".mcred-tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            // On Windows the file is created with the owner-only ACL, so it holds before a byte is written; the move keeps
            // it. (Setting it on the open stream afterwards needs WRITE_DAC, which a write handle lacks: access denied.)
            FileStream stream;
            if (OperatingSystem.IsWindows())
                stream = new FileInfo(temporary).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096,
                    FileOptions.None, OwnerOnlyFile());
            else
                stream = new FileStream(temporary, new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    UnixCreateMode = PrivateFile
                });
            using (stream)
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            MakePrivateFile(path);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public static void MakePrivateFile(string path)
    {
        RequireSupportedOS();
        if (OperatingSystem.IsWindows())
            new FileInfo(path).SetAccessControl(OwnerOnlyFile());
        else
            File.SetUnixFileMode(path, PrivateFile);
    }

    // The Windows counterpart of 0600 / 0700: inheritance off, one full-control rule for the current user. Cygwin maps
    // it to an owner-only mode, which its ssh requires of a private key.
    [SupportedOSPlatform("windows")]
    private static FileSecurity OwnerOnlyFile()
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity OwnerOnlyDirectory()
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentUser(), FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier CurrentUser()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return identity.User ?? throw new InvalidOperationException("The current Windows user has no security identifier.");
    }

    private static void RequireSupportedOS()
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("This version requires macOS or Windows.");
    }
}
