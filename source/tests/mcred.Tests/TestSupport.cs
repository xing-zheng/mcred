using System.Security.Cryptography;
using System.Text;
using Xunit;

// One environment variable and one root key store per process: the tests run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// A root key in memory, so no test reaches the Keychain or the Credential Manager.
internal sealed class MemoryRootKeyStore : IRootKeyStore
{
    private byte[]? encoded;

    public MemoryRootKeyStore(byte[]? rootKey = null)
    {
        if (rootKey is not null) encoded = Encoding.ASCII.GetBytes(Convert.ToBase64String(rootKey));
    }

    public byte[]? TryRead() => encoded?.ToArray();

    public void Add(byte[] value)
    {
        if (encoded is not null) throw new InvalidOperationException("Root key already exists; it was not changed.");
        encoded = value.ToArray();
    }
}

// A scratch repository: a temporary directory with .git, a seat's root key in memory, and the SSH clear tree variable.
internal sealed class Scratch : IDisposable
{
    private readonly IRootKeyStore previousStore;
    private readonly string? previousSshClear;

    public Scratch(bool withGit = true, bool withStore = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "mcred-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        if (withGit) Directory.CreateDirectory(Path.Combine(Root, ".git"));
        previousStore = RootKeyStore.Current;
        RootKey = RandomNumberGenerator.GetBytes(32);
        RootKeyStore.Current = new MemoryRootKeyStore(RootKey);
        previousSshClear = Environment.GetEnvironmentVariable(Config.SshClearVariable);
        SshClear = Path.Combine(Root, "..", Path.GetFileName(Root) + "-pem");
        Environment.SetEnvironmentVariable(Config.SshClearVariable, SshClear);
        if (withGit && withStore) Repository.Init();
    }

    public string Root { get; }
    public string SshClear { get; }
    public byte[] RootKey { get; }
    public Repository Repository => Repository.Find(Root);

    public string Write(string relative, string content)
    {
        string path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        RootKeyStore.Current = previousStore;
        Environment.SetEnvironmentVariable(Config.SshClearVariable, previousSshClear);
        Environment.SetEnvironmentVariable("MCRED_DECRYPTED_SSH_KEYS", null);
        foreach (string path in new[] { Root, Path.GetFullPath(SshClear) })
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
