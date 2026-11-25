using System.Security.Cryptography;
using System.Text.Json;

// Every line mcred prints ends in LF, on Windows too: callers parse `status` (`store=`) and the messages from Cygwin's
// bash, where a CRLF's CR stays in the value and the toolbox's store check refused the workspace's own store
// (2026-10-20). Set before anything is written.
Console.Out.NewLine = "\n";
Console.Error.NewLine = "\n";

try
{
    bool replace = args.Contains("--replace");
    string[] words = args.Where(arg => arg != "--replace").ToArray();
    if (replace && words.FirstOrDefault() is not ("add-ssh-key" or "add-ssl-key" or "decrypt-ssh-keys"))
        return Usage();
    int expiresHours = Seats.DefaultExpiresHours;
    int expiresAt = Array.IndexOf(words, "--expires-hours");
    if (expiresAt >= 0)
    {
        if (words is not ["seat", "invite", _, "--expires-hours", _] || !int.TryParse(words[4], out expiresHours))
            return Usage();
        words = words[..3];
    }

    if (words is ["--version"])
    {
        Console.Out.WriteLine(Config.Version);
    }
    else if (words is ["init"])
    {
        (string openedSlot, string openedStore) = Seats.InitExisting(Environment.CurrentDirectory, RootKeyDerivation.ReadPassword);
        Console.Error.WriteLine($"Root key saved; it opens slot '{openedSlot}' of {openedStore}.");
    }
    else if (words is ["init", "--new"])
    {
        Seats.InitNew(() =>
        {
            if (!Console.IsInputRedirected)
                Console.Error.WriteLine($"Choose this seat's password: at least {RootKeyDerivation.MinimumNewLength} characters, " +
                                        "best a passphrase of six random words. It is the seat's backup.");
            return RootKeyDerivation.ReadPassword();
        });
        Console.Error.WriteLine("Root key derived from the password and saved (macOS Keychain or Windows Credential Manager).");
    }
    else if (words is ["repo", "init"])
    {
        // Completes an existing store's own files; a new store names its first seat.
        Repository repository = Repository.Current();
        if (!Directory.Exists(repository.Store))
            throw new ArgumentException($"{repository.Root} has no store yet: name this seat, 'mcred repo init <seat>'.");
        IReadOnlyList<string> written = repository.Init();
        Console.Error.WriteLine(written.Count == 0
            ? $"{repository.Store} is complete; nothing written."
            : $"{repository.Store}: wrote {string.Join(", ", written.Select(Path.GetFileName))}.");
    }
    else if (words is ["repo", "init", string seat])
    {
        Repository repository = Repository.Current();
        Seats.InitStore(repository, seat);
        Console.Error.WriteLine($"{repository.Store}: a new store, its repository key opened by this seat's slot '{seat}'.");
    }
    else if (words is ["migrate", string migrateSeat])
    {
        Repository repository = Repository.Current();
        (int converted, int already) = Seats.Migrate(repository, migrateSeat);
        Console.Error.WriteLine($"{repository.Store} is format {repository.RequireStore().Format}: {converted} value(s) encrypted again as v3 " +
                                $"({already} already v3), each checked; this seat's slot is '{migrateSeat}'.");
    }
    else if (words is ["migrate"])
    {
        Repository repository = Repository.Current();
        (int Rows, int FromJson)? moved = new CredentialStore(repository).MigrateToCsv();
        Console.Error.WriteLine(moved is null
            ? $"{repository.Store} is format {Repository.CsvFormat} already, with no JSON file beside it; nothing written."
            : $"{repository.Store} is format {Repository.CsvFormat}: {moved.Value.Rows} credential(s) in " +
              $"{Path.GetFileName(repository.CredentialsCsvPath)}, {moved.Value.FromJson} of them from the JSON file, which is deleted. " +
              "Nothing was decrypted. Commit .mcred-store/.");
    }
    else if (words is ["seat", "list"])
    {
        foreach (string line in Seats.List(Repository.Current())) Console.Out.WriteLine(line);
    }
    else if (words is ["seat", "invite", string inviteName])
    {
        Repository repository = Repository.Current();
        string code = Seats.Invite(repository, inviteName, expiresHours);
        Console.Out.WriteLine(code);
        Console.Error.WriteLine($"The one-off root key above opens {repository.Store} for '{inviteName}' for {expiresHours} hour(s). " +
                                "Commit and push .mcred-store/repository.json, then give the key to them in private (face to face, " +
                                "a call, an end-to-end encrypted chat; never email, the repository or a ticket). " +
                                $"They run 'mcred seat join {inviteName}' in the repository.");
    }
    else if (words is ["seat", "join", string joinName])
    {
        Repository repository = Repository.Current();
        Seats.CheckInvite(repository, joinName);
        string? password = null;
        if (!RootKeyStore.Exists())
        {
            if (Console.IsInputRedirected)
                throw new InvalidOperationException("This seat has no root key: first run 'mcred init' with a password already in use, " +
                                                    "or 'mcred init --new' with a first password (on Windows in a PowerShell console).");
            Console.Error.WriteLine("This seat has no root key yet. If you use mcred on another seat, type that seat's password. If not, " +
                                    $"choose one: at least {RootKeyDerivation.MinimumLength} characters, best a passphrase of six random words.");
            password = RootKeyDerivation.ReadPassword();
            RootKeyDerivation.RequireLength(password, RootKeyDerivation.MinimumLength);
        }
        string code = Console.IsInputRedirected ? Console.In.ReadToEnd().Trim() : RootKeyDerivation.Prompt("One-off root key: ");
        Seats.Join(repository, joinName, code, password);
        Console.Error.WriteLine($"This seat joined {repository.Store} as '{joinName}'. Commit and push .mcred-store/repository.json.");
    }
    else if (words is ["seat", "remove", string removeName])
    {
        Repository repository = Repository.Current();
        Seats.Remove(repository, removeName);
        Console.Error.WriteLine($"Slot '{removeName}' removed. It keeps what it already read: run 'mcred rotate' for what is written next, " +
                                "and replace the secrets themselves for a real revocation. Commit .mcred-store/.");
    }
    else if (words is ["rotate"])
    {
        Repository repository = Repository.Current();
        (int count, IReadOnlyList<string> dropped) = Seats.Rotate(repository);
        Console.Error.WriteLine($"A new repository key: {count} value(s) encrypted again; the earlier keys kept under it.");
        if (dropped.Count > 0)
            Console.Error.WriteLine($"Dropped, to be invited again: {string.Join(", ", dropped)}.");
        Console.Error.WriteLine("Commit .mcred-store/ (the whole store changed).");
    }
    else if (words is ["status"])
    {
        Status.Print(Repository.Current());
    }
    else if (words is ["encrypt", string key, string cleartext])
    {
        var store = new CredentialStore(Repository.Current());
        byte[] plaintext = cleartext == "-" ? StdIo.ReadInput() : Config.Utf8.GetBytes(cleartext);
        try
        {
            store.Encrypt(key, plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
        Console.Error.WriteLine($"Encrypted credential '{key}' saved to {store.FilePath}.");
    }
    else if (words is ["list"] or ["list", _])
    {
        foreach (string listed in new CredentialStore(Repository.Current()).Names(words.Length == 2 ? words[1] : null))
            Console.Out.WriteLine(listed);
    }
    else if (words is ["rm", string removedKey])
    {
        var store = new CredentialStore(Repository.Current());
        store.Remove(removedKey);
        Console.Error.WriteLine($"Credential '{removedKey}' removed from {store.FilePath}.");
    }
    else if (words is ["mv", string oldKey, string newKey])
    {
        var store = new CredentialStore(Repository.Current());
        store.Move(oldKey, newKey);
        Console.Error.WriteLine($"Credential '{oldKey}' is now '{newKey}', encrypted again in {store.FilePath}.");
    }
    else if (words is ["decrypt", string name])
    {
        byte[] plaintext = new CredentialStore(Repository.Current()).Decrypt(name);
        try
        {
            StdIo.WriteOutput(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }
    else if (words is ["add-ssh-key" or "add-ssl-key", string file, ..] && words.Length <= 3)
    {
        KeyStoreKind kind = words[0] == "add-ssh-key" ? KeyStoreKind.Ssh : KeyStoreKind.Ssl;
        KeyStore.Add(Repository.Current(), kind, file, words.Length == 3 ? words[2] : null, replace);
        Console.Error.WriteLine($"{kind.Label} key copied and encrypted.");
    }
    else if (words is ["decrypt-ssh-keys" or "decrypt-ssl-keys"])
    {
        KeyStoreKind kind = words[0] == "decrypt-ssh-keys" ? KeyStoreKind.Ssh : KeyStoreKind.Ssl;
        (int written, int unchanged) = KeyStore.DecryptAll(Repository.Current(), kind, replace);
        Console.Error.WriteLine($"Decrypted {written + unchanged} {kind.Label} key file(s): {written} written, {unchanged} unchanged.");
    }
    else if (words is ["add-key" or "decrypt-keys", ..])
    {
        // Retired when the one key store became two (README.md, "Key stores").
        string verb = words[0] == "add-key" ? "add" : "decrypt";
        string keys = words[0] == "add-key" ? "key" : "keys";
        Console.Error.WriteLine($"mcred: {words[0]} is retired - use {verb}-ssh-{keys} or {verb}-ssl-{keys}");
        return 2;
    }
    else
    {
        return Usage();
    }
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or
                              InvalidDataException or InvalidOperationException or ArgumentException or
                              CryptographicException or PlatformNotSupportedException or FormatException or JsonException)
{
    Console.Error.WriteLine($"mcred: {error.Message}");
    return 1;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: mcred init [--new] | repo init [SEAT] | migrate [SEAT] | status | --version | " +
                            "encrypt KEY CLEARTEXT|- | decrypt KEY | list [PREFIX] | rm KEY | mv OLD NEW | " +
                            "add-ssh-key KEY_FILE [SUBDIRECTORY] [--replace] | add-ssl-key KEY_FILE [SUBDIRECTORY] | " +
                            "decrypt-ssh-keys [--replace] | decrypt-ssl-keys | seat list | " +
                            "seat invite NAME [--expires-hours N] | seat join NAME | seat remove NAME | rotate");
    return 2;
}
