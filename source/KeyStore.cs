using System.Security.Cryptography;

// The two key stores: a committed ciphertext tree in the repository's store, and a clear tree - the SSH keys' in the
// one variable (shared by every repository, so a differing file is never replaced silently), the SSL keys' in the store
// itself (ignored by git). The prefix is the start of every file's authenticated name, so a ciphertext moved from one
// store into the other does not decrypt.
internal sealed record KeyStoreKind(string Label, string AuthenticatedPrefix, bool SharedClearTree)
{
    // "key-file:" is what the SSH store's files were encrypted under on 2026-09-28 (as .encrypted-keys/): kept, so the
    // store's moves re-encrypted nothing.
    public static readonly KeyStoreKind Ssh = new("SSH", "key-file:", SharedClearTree: true);
    public static readonly KeyStoreKind Ssl = new("SSL", "ssl-key-file:", SharedClearTree: false);
}

internal static class KeyStore
{
    public static void Add(Repository repository, KeyStoreKind kind, string sourcePath, string? subdirectory, bool replace)
    {
        string source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new FileNotFoundException($"Key file was not found or is a symbolic link: {source}");
        string relativeDirectory = ValidateSubdirectory(subdirectory);
        string relative = Path.Combine(relativeDirectory, Path.GetFileName(source));
        (string encryptedRoot, string decryptedRoot) = Roots(repository, kind);
        string encrypted = Path.Combine(encryptedRoot, relative);
        string decrypted = Path.Combine(decryptedRoot, relative);
        byte[] plaintext = File.ReadAllBytes(source);
        try
        {
            bool sameFile = string.Equals(source, decrypted, StringComparison.Ordinal);
            if (!sameFile && kind.SharedClearTree && !replace && File.Exists(decrypted) &&
                !File.ReadAllBytes(decrypted).AsSpan().SequenceEqual(plaintext))
                throw new InvalidOperationException(
                    $"{decrypted} exists and differs from {source}: nothing written. Add it with --replace after a look.");
            string ciphertext = Ciphers.Open(repository).Encrypt(plaintext, AuthenticatedName(kind, relative));
            Config.AtomicWrite(encrypted, Config.Utf8.GetBytes(ciphertext));
            if (!sameFile)
                Config.AtomicWrite(decrypted, plaintext);
            else
                Config.MakePrivateFile(decrypted);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    // Decrypts every file of the store in memory first, so a refusal writes nothing: in the shared SSH tree a file that
    // differs from the store's is replaced only with --replace.
    public static (int Written, int Unchanged) DecryptAll(Repository repository, KeyStoreKind kind, bool replace)
    {
        (string encryptedRoot, string decryptedRoot) = Roots(repository, kind);
        IValueCipher cipher = Ciphers.Open(repository);
        var files = new List<(string Relative, byte[] Plaintext)>();
        try
        {
            foreach (string relative in EncryptedFiles(encryptedRoot, kind))
            {
                string value = Config.Utf8.GetString(File.ReadAllBytes(Path.Combine(encryptedRoot, relative)));
                files.Add((relative, cipher.Decrypt(value, AuthenticatedName(kind, relative))));
            }
            var differing = new List<string>();
            var pending = new List<(string Relative, byte[] Plaintext)>();
            int unchanged = 0;
            foreach ((string relative, byte[] plaintext) in files)
            {
                string target = Path.Combine(decryptedRoot, relative);
                if (!File.Exists(target))
                    pending.Add((relative, plaintext));
                else if (File.ReadAllBytes(target).AsSpan().SequenceEqual(plaintext))
                    unchanged++;
                else
                {
                    differing.Add(relative.Replace('\\', '/'));
                    pending.Add((relative, plaintext));
                }
            }
            if (kind.SharedClearTree && !replace && differing.Count > 0)
                throw new InvalidOperationException(
                    $"{differing.Count} file(s) in {decryptedRoot} differ from the {kind.Label} store: {string.Join(", ", differing)}. " +
                    $"Nothing written. Run 'mcred decrypt-{kind.Label.ToLowerInvariant()}-keys --replace' after a look.");
            foreach ((string relative, byte[] plaintext) in pending)
                Config.AtomicWrite(Path.Combine(decryptedRoot, relative), plaintext);
            return (pending.Count, unchanged);
        }
        finally
        {
            foreach ((_, byte[] plaintext) in files) CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static int Count(Repository repository, KeyStoreKind kind)
    {
        string root = EncryptedRoot(repository, kind);
        return Directory.Exists(root) ? EncryptedFiles(root, kind).Count : 0;
    }

    // The kind's two roots. The SSH clear tree (the variable) may not be, or lie inside, the store, nor hold the store;
    // the SSL clear tree is the store's own, written only while .mcred-store/.gitignore ignores it.
    private static (string Encrypted, string Decrypted) Roots(Repository repository, KeyStoreKind kind)
    {
        repository.RequireStore();
        string encrypted = EncryptedRoot(repository, kind);
        if (!kind.SharedClearTree)
        {
            repository.RequireSslIgnored();
            return (encrypted, repository.DecryptedSslKeys);
        }
        string decrypted = Config.RequiredSshClearRoot();
        if (Config.IsSameOrInside(decrypted, repository.Store))
            throw new InvalidOperationException($"{Config.SshClearVariable} must not be {repository.Store} or lie inside it.");
        if (Config.IsSameOrInside(repository.Store, decrypted))
            throw new InvalidOperationException($"{Config.SshClearVariable} must not hold the store {repository.Store}.");
        return (encrypted, decrypted);
    }

    internal static string EncryptedRoot(Repository repository, KeyStoreKind kind) =>
        kind.SharedClearTree ? repository.EncryptedSshKeys : repository.EncryptedSslKeys;

    // Every ciphertext under the root, as relative paths in ordinal order; .DS_Store is skipped, a symbolic link refused.
    internal static List<string> EncryptedFiles(string encryptedRoot, KeyStoreKind kind)
    {
        if (!Directory.Exists(encryptedRoot))
            throw new DirectoryNotFoundException($"Encrypted {kind.Label} keys directory was not found: {encryptedRoot}");
        if ((File.GetAttributes(encryptedRoot) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Encrypted {kind.Label} keys directory is a symbolic link: {encryptedRoot}");
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(encryptedRoot);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (Path.GetFileName(entry) == ".DS_Store") continue;
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException($"Encrypted {kind.Label} keys directory contains a symbolic link: {entry}");
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
                else
                    result.Add(Path.GetRelativePath(encryptedRoot, entry));
            }
        }
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    internal static string AuthenticatedName(KeyStoreKind kind, string relative) => kind.AuthenticatedPrefix + relative.Replace('\\', '/');

    private static string ValidateSubdirectory(string? value)
    {
        if (string.IsNullOrEmpty(value) || value == ".") return string.Empty;
        if (Path.IsPathRooted(value) || value.Contains('\\') ||
            value.Split('/').Any(part => part is "" or "." or ".."))
            throw new ArgumentException("Subdirectory must be a relative path without empty, '.' or '..' components.");
        return value;
    }
}
