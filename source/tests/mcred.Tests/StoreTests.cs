using System.Text;
using Xunit;

public class StoreTests
{
    [Fact]
    public void ACredentialRoundTripsAndTheJsonHasNoCarriageReturn()
    {
        using var scratch = new Scratch();
        var store = new CredentialStore(scratch.Repository);
        store.Encrypt("b/name", Encoding.UTF8.GetBytes("value+with/plus\n"));
        store.Encrypt("a/name", Encoding.UTF8.GetBytes("é"));
        Assert.Equal("value+with/plus\n", Encoding.UTF8.GetString(store.Decrypt("b/name")));
        Assert.Equal("é", Encoding.UTF8.GetString(store.Decrypt("a/name")));
        string json = File.ReadAllText(store.FilePath);
        Assert.DoesNotContain("\r", json);
        Assert.True(json.IndexOf("\"a/name\"", StringComparison.Ordinal) < json.IndexOf("\"b/name\"", StringComparison.Ordinal));
        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void ACredentialUnderAnotherRootKeyDoesNotDecrypt()
    {
        using var scratch = new Scratch();
        var store = new CredentialStore(scratch.Repository);
        store.Encrypt("name", Encoding.UTF8.GetBytes("value"));
        RootKeyStore.Current = new MemoryRootKeyStore(new byte[32]);
        Assert.Throws<InvalidDataException>(() => store.Decrypt("name"));
    }

    [Fact]
    public void AMissingRootKeyNamesInit()
    {
        using var scratch = new Scratch();
        RootKeyStore.Current = new MemoryRootKeyStore();
        var error = Assert.Throws<InvalidOperationException>(() =>
            new CredentialStore(scratch.Repository).Encrypt("name", Encoding.UTF8.GetBytes("value")));
        Assert.Contains("mcred init", error.Message);
    }

    [Fact]
    public void TheSshTreeRefusesADifferingFileAndWritesNothing()
    {
        using var scratch = new Scratch();
        string a = scratch.Write("src/a.pem", "A");
        string b = scratch.Write("src/b.pem", "B");
        KeyStore.Add(scratch.Repository, KeyStoreKind.Ssh, a, null, replace: false);
        KeyStore.Add(scratch.Repository, KeyStoreKind.Ssh, b, "sub", replace: false);
        Assert.Equal((0, 2), KeyStore.DecryptAll(scratch.Repository, KeyStoreKind.Ssh, replace: false));

        File.WriteAllText(Path.Combine(scratch.SshClear, "a.pem"), "changed");
        File.Delete(Path.Combine(scratch.SshClear, "sub", "b.pem"));
        var error = Assert.Throws<InvalidOperationException>(() => KeyStore.DecryptAll(scratch.Repository, KeyStoreKind.Ssh, replace: false));
        Assert.Contains("a.pem", error.Message);
        Assert.False(File.Exists(Path.Combine(scratch.SshClear, "sub", "b.pem")));
        Assert.Equal("changed", File.ReadAllText(Path.Combine(scratch.SshClear, "a.pem")));

        Assert.Equal((2, 0), KeyStore.DecryptAll(scratch.Repository, KeyStoreKind.Ssh, replace: true));
        Assert.Equal("A", File.ReadAllText(Path.Combine(scratch.SshClear, "a.pem")));
        Assert.Equal(2, KeyStore.Count(scratch.Repository, KeyStoreKind.Ssh));
    }

    [Fact]
    public void AddSshKeyRefusesToReplaceADifferentClearCopy()
    {
        using var scratch = new Scratch();
        Directory.CreateDirectory(scratch.SshClear);
        File.WriteAllText(Path.Combine(scratch.SshClear, "a.pem"), "other repository's key");
        string a = scratch.Write("src/a.pem", "A");
        Assert.Throws<InvalidOperationException>(() => KeyStore.Add(scratch.Repository, KeyStoreKind.Ssh, a, null, replace: false));
        Assert.False(File.Exists(Path.Combine(scratch.Repository.EncryptedSshKeys, "a.pem")));
        KeyStore.Add(scratch.Repository, KeyStoreKind.Ssh, a, null, replace: true);
        Assert.Equal("A", File.ReadAllText(Path.Combine(scratch.SshClear, "a.pem")));
    }

    [Fact]
    public void AnSshCiphertextMovedIntoTheSslStoreDoesNotDecrypt()
    {
        using var scratch = new Scratch();
        string a = scratch.Write("src/a.pem", "A");
        KeyStore.Add(scratch.Repository, KeyStoreKind.Ssh, a, null, replace: false);
        Directory.CreateDirectory(scratch.Repository.EncryptedSslKeys);
        File.Copy(Path.Combine(scratch.Repository.EncryptedSshKeys, "a.pem"), Path.Combine(scratch.Repository.EncryptedSslKeys, "a.pem"));
        Assert.Throws<InvalidDataException>(() => KeyStore.DecryptAll(scratch.Repository, KeyStoreKind.Ssl, replace: false));
    }

    [Fact]
    public void TheSslTreeNeedsItsIgnoreLine()
    {
        using var scratch = new Scratch();
        string a = scratch.Write("src/a.pem", "A");
        KeyStore.Add(scratch.Repository, KeyStoreKind.Ssl, a, "site", replace: false);
        Assert.True(File.Exists(Path.Combine(scratch.Repository.DecryptedSslKeys, "site", "a.pem")));
        File.WriteAllText(scratch.Repository.GitIgnorePath, "# nothing\n");
        Assert.Throws<InvalidOperationException>(() => KeyStore.DecryptAll(scratch.Repository, KeyStoreKind.Ssl, replace: false));
    }

    [Fact]
    public void TheSshClearTreeMayNotLieInsideTheStoreNorHoldIt()
    {
        using var scratch = new Scratch();
        string a = scratch.Write("src/a.pem", "A");
        Environment.SetEnvironmentVariable(Config.SshClearVariable, Path.Combine(scratch.Repository.Store, "pem"));
        Assert.Throws<InvalidOperationException>(() => KeyStore.Add(scratch.Repository, KeyStoreKind.Ssh, a, null, replace: false));
        Environment.SetEnvironmentVariable(Config.SshClearVariable, scratch.Root);
        Assert.Throws<InvalidOperationException>(() => KeyStore.Add(scratch.Repository, KeyStoreKind.Ssh, a, null, replace: false));
    }

    [Fact]
    public void TheRetiredVariableAloneNamesTheNewOne()
    {
        using var scratch = new Scratch();
        Environment.SetEnvironmentVariable(Config.SshClearVariable, null);
        Environment.SetEnvironmentVariable("MCRED_DECRYPTED_SSH_KEYS", "/tmp/pem");
        var error = Assert.Throws<InvalidOperationException>(() => KeyStore.DecryptAll(scratch.Repository, KeyStoreKind.Ssh, replace: false));
        Assert.Contains(Config.SshClearVariable, error.Message);
        Assert.Contains("no longer read", error.Message);
    }
}
