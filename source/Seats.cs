using System.Security.Cryptography;
using System.Text.RegularExpressions;

// Formats 2 and 3: one random repository key per repository, opened through slots (LUKS-like, symmetric): a seat slot under a
// seat's root key, a one-off slot under a one-off root key that a holder hands to a new developer, who uses it once to
// put a seat slot under their own root key in its place. Only a root key can make or open its own slot, so push access
// alone cannot add a slot that a seat would open.
internal static partial class Seats
{
    public const int OneOffKeyLength = 16;
    public const int DefaultExpiresHours = 72;
    public const int MaximumExpiresHours = 168;

    // The test assembly sets the clock.
    internal static Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,62}$")]
    private static partial Regex NamePattern();

    public static string SeatKeyId(byte[] rootKey) => Crypto.KeyId(rootKey, Crypto.SeatKeyLabel);

    private static string Stamp(DateTimeOffset time) =>
        time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseStamp(string text) =>
        DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal);

    private static string SlotData(RepositoryMetadata metadata, string repositoryKeyId, string name, string kind, string keyId,
        string? expires) => $"s1:{metadata.Id}:{name}:{kind}:{keyId}:{expires ?? ""}:{repositoryKeyId}";

    private static string EarlierData(RepositoryMetadata metadata, string earlierKeyId, string currentKeyId) =>
        $"h1:{metadata.Id}:{earlierKeyId}:{currentKeyId}";

    private static Slot SealSlot(RepositoryMetadata metadata, string repositoryKeyId, byte[] repositoryKey, string name,
        string kind, byte[] slotKey, string keyId, string addedBy, string? expires) =>
        new(name, kind, keyId, Stamp(Now()), addedBy, expires,
            Crypto.Seal("s1", slotKey, Crypto.SlotInfo, repositoryKey,
                SlotData(metadata, repositoryKeyId, name, kind, keyId, expires)));

    private static byte[] OpenSlot(RepositoryMetadata metadata, Slot slot, byte[] slotKey)
    {
        byte[] key = Crypto.Open("s1", slotKey, Crypto.SlotInfo, slot.Wrapped,
            SlotData(metadata, metadata.RepositoryKeyId, slot.Name, slot.Kind, slot.KeyId, slot.Expires), $"slot '{slot.Name}'");
        if (Crypto.KeyId(key, Crypto.RepositoryKeyLabel) != metadata.RepositoryKeyId)
            throw new InvalidDataException($"Slot '{slot.Name}' holds a key other than the repository key {metadata.RepositoryKeyId}.");
        return key;
    }

    private static void ValidateName(string name)
    {
        if (!NamePattern().IsMatch(name))
            throw new ArgumentException($"A seat's name is 1-63 of a-z, 0-9 and '-', starting with a letter or digit: '{name}'.");
    }

    // This seat's slot and the repository key it opens; a seat without a slot is told how to get one.
    public static (Slot Slot, byte[] RepositoryKey) OpenOwn(Repository repository, RepositoryMetadata metadata)
    {
        if (metadata.Format < 2)
            throw new InvalidOperationException($"{repository.Store} is format 1 (no seats yet): run 'mcred migrate <seat>' first.");
        byte[] rootKey = RootKeyStore.ReadRootKey();
        try
        {
            string keyId = SeatKeyId(rootKey);
            Slot slot = metadata.Slots.FirstOrDefault(s => s.Kind == Slot.SeatKind && s.KeyId == keyId)
                ?? throw new InvalidOperationException(
                    $"This seat's root key has no slot in {repository.Store}. Ask a holder for a one-off root key " +
                    "('mcred seat invite <name>'), then run 'mcred seat join <name>'.");
            return (slot, OpenSlot(metadata, slot, rootKey));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
        }
    }

    public static RepositoryKeyCipher OpenCipher(Repository repository, RepositoryMetadata metadata)
    {
        (_, byte[] key) = OpenOwn(repository, metadata);
        return CipherFor(metadata, key);
    }

    private static RepositoryKeyCipher CipherFor(RepositoryMetadata metadata, byte[] key)
    {
        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [metadata.RepositoryKeyId] = key };
        foreach (EarlierKey earlier in metadata.EarlierKeys)
            keys[earlier.RepositoryKeyId] = Crypto.Open("h1", key, Crypto.EarlierKeyInfo, earlier.Wrapped,
                EarlierData(metadata, earlier.RepositoryKeyId, metadata.RepositoryKeyId), "earlier repository key");
        return new RepositoryKeyCipher(key, metadata.RepositoryKeyId, keys);
    }

    // mcred repo init <seat>: a new store in the newest format, its first slot this seat's.
    public static void InitStore(Repository repository, string seat)
    {
        ValidateName(seat);
        if (File.Exists(repository.MetadataPath))
        {
            RepositoryMetadata existing = RepositoryMetadata.Load(repository.MetadataPath);
            throw new InvalidOperationException(existing.Format == 1
                ? $"{repository.Store} already exists in format 1: run 'mcred migrate {seat}' to give it seats."
                : $"{repository.Store} already exists; ask a holder for a one-off root key instead.");
        }
        byte[] rootKey = RootKeyStore.ReadRootKey();
        byte[] repositoryKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var metadata = new RepositoryMetadata { Format = Repository.Format, Id = Guid.NewGuid().ToString("D") };
            string keyId = Crypto.KeyId(repositoryKey, Crypto.RepositoryKeyLabel);
            var created = new RepositoryMetadata
            {
                Format = Repository.Format, Id = metadata.Id, RepositoryKeyId = keyId,
                Slots = [SealSlot(metadata, keyId, repositoryKey, seat, Slot.SeatKind, rootKey, SeatKeyId(rootKey), seat, null)],
            };
            repository.Init(created);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
            CryptographicOperations.ZeroMemory(repositoryKey);
        }
    }

    // mcred migrate <seat>: a format-1 store (v2 under this seat's root key) into format 2. Every value is decrypted
    // before anything is written, and nothing is written if one fails; the metadata is written first, so a run that
    // stops is finished by running it again (only migrate reads v2 in format 2).
    public static (int Values, int AlreadyV3) Migrate(Repository repository, string seat)
    {
        ValidateName(seat);
        RepositoryMetadata metadata = repository.RequireStore();
        byte[] rootKey = RootKeyStore.ReadRootKey();
        byte[]? repositoryKey = null;
        try
        {
            if (metadata.Format == 1)
            {
                List<StoreValue> values = StoreValue.All(repository);
                var plaintexts = new List<byte[]>();
                try
                {
                    foreach (StoreValue value in values)
                        plaintexts.Add(Encryption.DecryptV2(rootKey, value.Text, value.Name));
                }
                finally
                {
                    foreach (byte[] plaintext in plaintexts) CryptographicOperations.ZeroMemory(plaintext);
                }
                repositoryKey = RandomNumberGenerator.GetBytes(32);
                string keyId = Crypto.KeyId(repositoryKey, Crypto.RepositoryKeyLabel);
                metadata = new RepositoryMetadata
                {
                    Format = 2, Id = metadata.Id, RepositoryKeyId = keyId,
                    Slots = [SealSlot(metadata, keyId, repositoryKey, seat, Slot.SeatKind, rootKey, SeatKeyId(rootKey), seat, null)],
                };
                metadata.Save(repository.MetadataPath);
            }
            else
            {
                (_, repositoryKey) = OpenOwn(repository, metadata);
            }
            RepositoryKeyCipher cipher = CipherFor(metadata, repositoryKey);
            int converted = 0, already = 0;
            StoreValue.Rewrite(repository, value =>
            {
                if (!value.Text.StartsWith("v2.", StringComparison.Ordinal)) { already++; return value.Text; }
                byte[] plaintext = Encryption.DecryptV2(rootKey, value.Text, value.Name);
                try
                {
                    string v3 = cipher.Encrypt(plaintext, value.Name);
                    byte[] check = cipher.Decrypt(v3, value.Name);
                    bool same = check.AsSpan().SequenceEqual(plaintext);
                    CryptographicOperations.ZeroMemory(check);
                    if (!same) throw new CryptographicException($"'{value.Name}' did not decrypt back to its value.");
                    converted++;
                    return v3;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            });
            return (converted, already);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
            if (repositoryKey is not null) CryptographicOperations.ZeroMemory(repositoryKey);
        }
    }

    // mcred seat invite <name>: a one-off root key for a new developer; its slot expires (the expiry is authenticated).
    public static string Invite(Repository repository, string name, int expiresHours)
    {
        ValidateName(name);
        if (expiresHours is < 1 or > MaximumExpiresHours)
            throw new ArgumentException($"--expires-hours must be 1 to {MaximumExpiresHours}.");
        RepositoryMetadata metadata = repository.RequireStore();
        (Slot own, byte[] repositoryKey) = OpenOwn(repository, metadata);
        byte[] code = RandomNumberGenerator.GetBytes(OneOffKeyLength);
        try
        {
            if (metadata.Slots.Any(s => s.Name == name))
                throw new InvalidOperationException($"A slot named '{name}' exists already ('mcred seat list').");
            string expires = Stamp(Now().AddHours(expiresHours));
            metadata.Slots.Add(SealSlot(metadata, metadata.RepositoryKeyId, repositoryKey, name, Slot.OneOffKind, code,
                Crypto.KeyId(code, Crypto.OneOffKeyLabel), own.Name, expires));
            metadata.Save(repository.MetadataPath);
            return Crypto.ToCode(code);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(code);
            CryptographicOperations.ZeroMemory(repositoryKey);
        }
    }

    // The one-off slot <name>, or why it cannot be joined. The caller checks it before it asks the person for anything.
    public static void CheckInvite(Repository repository, string name)
    {
        ValidateName(name);
        FindInvite(repository, repository.RequireStore(), name);
    }

    private static Slot FindInvite(Repository repository, RepositoryMetadata metadata, string name)
    {
        if (metadata.Format < 2)
            throw new InvalidOperationException($"{repository.Store} is format 1: it has no seats to join.");
        Slot invite = metadata.Slots.FirstOrDefault(s => s.Name == name && s.Kind == Slot.OneOffKind)
            ?? throw new InvalidOperationException($"No one-off root key named '{name}' in {repository.Store} (pull, or ask the holder).");
        if (invite.Expires is null || ParseStamp(invite.Expires) <= Now())
            throw new InvalidOperationException($"The one-off root key '{name}' expired at {invite.Expires}: ask the holder for a new one.");
        return invite;
    }

    // mcred seat join <name>: this seat opens the one-off slot <name> with the code and puts its own seat slot in its
    // place, under the same name. A seat with no root key passes its password: the root key is derived and saved only
    // once the code has opened the slot, so a failed join saves nothing.
    public static void Join(Repository repository, string name, string codeText, string? password = null)
    {
        ValidateName(name);
        RepositoryMetadata metadata = repository.RequireStore();
        Slot invite = FindInvite(repository, metadata, name);
        byte[] code = Crypto.FromCode(codeText, OneOffKeyLength);
        byte[]? rootKey = null;
        byte[]? repositoryKey = null;
        try
        {
            if (Crypto.KeyId(code, Crypto.OneOffKeyLabel) != invite.KeyId)
                throw new InvalidDataException($"That is not the one-off root key '{name}'.");
            repositoryKey = OpenSlot(metadata, invite, code);
            string? encoded = password is null ? null : RootKeyDerivation.Derive(password);
            rootKey = encoded is null ? RootKeyStore.ReadRootKey() : RootKeyStore.DecodeRootKey(encoded);
            string seatKeyId = SeatKeyId(rootKey);
            Slot? existing = metadata.Slots.FirstOrDefault(s => s.Kind == Slot.SeatKind && s.KeyId == seatKeyId);
            if (existing is not null)
                throw new InvalidOperationException(encoded is null
                    ? $"This seat's root key already has the slot '{existing.Name}'."
                    : $"That password already has the slot '{existing.Name}' here: run 'mcred init' with it. Nothing was saved.");
            metadata.Slots[metadata.Slots.IndexOf(invite)] = SealSlot(metadata, metadata.RepositoryKeyId, repositoryKey, name,
                Slot.SeatKind, rootKey, seatKeyId, invite.AddedBy, null);
            // The root key first: if the metadata's write then fails, the one-off slot is still there for a second join.
            if (encoded is not null) RootKeyStore.Initialize(encoded);
            metadata.Save(repository.MetadataPath);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(code);
            if (rootKey is not null) CryptographicOperations.ZeroMemory(rootKey);
            if (repositoryKey is not null) CryptographicOperations.ZeroMemory(repositoryKey);
        }
    }

    // mcred init: a password already in use comes onto this seat. Its root key is saved only if it opens a seat slot of
    // the store here: a new password typed by mistake would open none of the person's slots, and a root key is never
    // replaced. Where there is nothing to check against, nothing is asked. A first password is 'mcred init --new'.
    public static (string Slot, string Store) InitExisting(string directory, Func<string> readPassword)
    {
        RefuseExistingRootKey();
        Repository repository = Repository.TryFind(directory)
            ?? throw NothingToCheck($"No repository here ({Path.GetFullPath(directory)})");
        if (repository.HasOldStore)
            throw NothingToCheck($"{repository.Root} keeps its store in {Repository.OldStoreName}/, which this mcred does not read");
        if (!File.Exists(repository.MetadataPath))
            throw NothingToCheck($"{repository.Root} has no store");
        RepositoryMetadata metadata = repository.RequireStore();
        if (metadata.Format < 2)
            throw NothingToCheck($"{repository.Store} is format 1 and has no slots");
        string encoded = RootKeyDerivation.Derive(readPassword());
        byte[] rootKey = RootKeyStore.DecodeRootKey(encoded);
        try
        {
            string keyId = SeatKeyId(rootKey);
            Slot slot = metadata.Slots.FirstOrDefault(s => s.Kind == Slot.SeatKind && s.KeyId == keyId)
                ?? throw new InvalidOperationException(
                    $"This password opens no slot of {repository.Store} (its seats: " +
                    $"{string.Join(", ", metadata.Slots.Where(s => s.Kind == Slot.SeatKind).Select(s => s.Name))}). Nothing was saved. " +
                    "If you use mcred on another seat, run 'mcred init' again with that seat's password. A new developer asks a " +
                    "holder for a one-off root key and runs 'mcred seat join <name>'. A first password is 'mcred init --new'.");
            CryptographicOperations.ZeroMemory(OpenSlot(metadata, slot, rootKey));
            RootKeyStore.Initialize(encoded);
            return (slot.Name, repository.Store);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(rootKey);
        }
    }

    // mcred init --new: a first password, in any directory, with nothing to check it against.
    public static void InitNew(Func<string> readPassword)
    {
        RefuseExistingRootKey();
        string password = readPassword();
        RootKeyDerivation.RequireLength(password, RootKeyDerivation.MinimumNewLength);
        RootKeyStore.Initialize(RootKeyDerivation.Derive(password));
    }

    private static void RefuseExistingRootKey()
    {
        if (RootKeyStore.Exists())
            throw new InvalidOperationException("This seat already has a root key; it was not changed.");
    }

    private static InvalidOperationException NothingToCheck(string where) => new(
        $"{where}, so there is no slot to check a password against. Nothing was saved. Run 'mcred init' in a repository " +
        "whose store has this password's slot. A first password is 'mcred init --new'.");

    // mcred seat remove <name>: any slot but the last seat slot. A removed seat keeps what it read (rotate, and replace
    // the secrets themselves, for a real revocation).
    public static void Remove(Repository repository, string name)
    {
        RepositoryMetadata metadata = repository.RequireStore();
        (_, byte[] repositoryKey) = OpenOwn(repository, metadata);
        CryptographicOperations.ZeroMemory(repositoryKey);
        Slot slot = metadata.Slots.FirstOrDefault(s => s.Name == name)
            ?? throw new InvalidOperationException($"No slot named '{name}' ('mcred seat list').");
        if (slot.Kind == Slot.SeatKind && metadata.Slots.Count(s => s.Kind == Slot.SeatKind) == 1)
            throw new InvalidOperationException($"'{name}' is the last seat slot: removing it would lock the store.");
        metadata.Slots.Remove(slot);
        metadata.Save(repository.MetadataPath);
    }

    // mcred rotate: a new repository key. The metadata is written first, with the old keys kept under the new one, so
    // every value opens at every moment; then every value is encrypted again. Only this seat's slot can be made again:
    // the other slots are dropped and their names returned, to be invited again.
    public static (int Values, IReadOnlyList<string> Dropped) Rotate(Repository repository)
    {
        RepositoryMetadata metadata = repository.RequireStore();
        (Slot own, byte[] oldKey) = OpenOwn(repository, metadata);
        RepositoryKeyCipher oldCipher = CipherFor(metadata, oldKey);
        byte[] rootKey = RootKeyStore.ReadRootKey();
        byte[] newKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            string newKeyId = Crypto.KeyId(newKey, Crypto.RepositoryKeyLabel);
            var earlier = new List<EarlierKey>
            {
                new(metadata.RepositoryKeyId, Crypto.Seal("h1", newKey, Crypto.EarlierKeyInfo, oldKey,
                    EarlierData(metadata, metadata.RepositoryKeyId, newKeyId))),
            };
            foreach (EarlierKey older in metadata.EarlierKeys)
            {
                byte[] olderKey = Crypto.Open("h1", oldKey, Crypto.EarlierKeyInfo, older.Wrapped,
                    EarlierData(metadata, older.RepositoryKeyId, metadata.RepositoryKeyId), "earlier repository key");
                earlier.Add(new EarlierKey(older.RepositoryKeyId, Crypto.Seal("h1", newKey, Crypto.EarlierKeyInfo, olderKey,
                    EarlierData(metadata, older.RepositoryKeyId, newKeyId))));
                CryptographicOperations.ZeroMemory(olderKey);
            }
            var dropped = metadata.Slots.Where(s => s != own).Select(s => s.Name).ToList();
            var rotated = new RepositoryMetadata
            {
                Format = metadata.Format, Id = metadata.Id, RepositoryKeyId = newKeyId, EarlierKeys = earlier,
                Slots = [SealSlot(metadata, newKeyId, newKey, own.Name, Slot.SeatKind, rootKey, own.KeyId, own.AddedBy, null)],
            };
            rotated.Save(repository.MetadataPath);
            RepositoryKeyCipher newCipher = CipherFor(rotated, newKey);
            int count = 0;
            StoreValue.Rewrite(repository, value =>
            {
                byte[] plaintext = oldCipher.Decrypt(value.Text, value.Name);
                try
                {
                    count++;
                    return newCipher.Encrypt(plaintext, value.Name);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            });
            return (count, dropped);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(oldKey);
            CryptographicOperations.ZeroMemory(rootKey);
            CryptographicOperations.ZeroMemory(newKey);
        }
    }

    public static IEnumerable<string> List(Repository repository)
    {
        RepositoryMetadata metadata = repository.RequireStore();
        if (metadata.Format < 2)
            throw new InvalidOperationException($"{repository.Store} is format 1: it has no seats yet ('mcred migrate <seat>').");
        byte[]? rootKey = RootKeyStore.TryReadRootKey();
        string? own = rootKey is null ? null : SeatKeyId(rootKey);
        if (rootKey is not null) CryptographicOperations.ZeroMemory(rootKey);
        yield return "NAME\tKIND\tADDED\tBY\tEXPIRES";
        foreach (Slot slot in metadata.Slots)
            yield return $"{slot.Name}\t{slot.Kind}\t{slot.Added}\t{slot.AddedBy}\t{slot.Expires ?? "-"}" +
                         (slot.Kind == Slot.SeatKind && slot.KeyId == own ? "\t(this seat)" : "");
    }

    // The status lines of a store with slots: nothing is decrypted, the root key only names this seat's slot.
    public static IEnumerable<(string Key, string Value)> StatusLines(Repository repository, RepositoryMetadata metadata)
    {
        byte[]? rootKey = RootKeyStore.TryReadRootKey();
        string? own = rootKey is null ? null : SeatKeyId(rootKey);
        if (rootKey is not null) CryptographicOperations.ZeroMemory(rootKey);
        List<StoreValue> values = StoreValue.All(repository);
        DateTimeOffset now = Now();
        yield return ("repository_key", metadata.RepositoryKeyId);
        yield return ("seat", metadata.Slots.FirstOrDefault(s => s.Kind == Slot.SeatKind && s.KeyId == own)?.Name ?? "none");
        yield return ("slots", metadata.Slots.Count.ToString());
        yield return ("values_v2", values.Count(v => v.Text.StartsWith("v2.", StringComparison.Ordinal)).ToString());
        yield return ("values_earlier_key", values.Count(v => v.Text.StartsWith("v3.", StringComparison.Ordinal) &&
                                                              Crypto.ValueKeyId(v.Text) != metadata.RepositoryKeyId).ToString());
        yield return ("one_off_expired", metadata.Slots.Count(s => s.Kind == Slot.OneOffKind && s.Expires is not null &&
                                                                   ParseStamp(s.Expires) <= now).ToString());
    }
}

// Every value of a store: the credentials and the files of both key stores, by their authenticated names.
internal sealed record StoreValue(string Name, string Text, string? FilePath)
{
    public static List<StoreValue> All(Repository repository)
    {
        var values = new CredentialStore(repository).Rows().Select(row => new StoreValue(row.Key, row.Value, null)).ToList();
        values.AddRange(KeyFiles(repository));
        return values;
    }

    public static List<StoreValue> KeyFiles(Repository repository)
    {
        var values = new List<StoreValue>();
        foreach (KeyStoreKind kind in new[] { KeyStoreKind.Ssh, KeyStoreKind.Ssl })
        {
            string root = KeyStore.EncryptedRoot(repository, kind);
            if (!Directory.Exists(root)) continue;
            foreach (string relative in KeyStore.EncryptedFiles(root, kind))
            {
                string path = Path.Combine(root, relative);
                values.Add(new StoreValue(KeyStore.AuthenticatedName(kind, relative), Config.Utf8.GetString(File.ReadAllBytes(path)), path));
            }
        }
        return values;
    }

    // Maps every value to its new text: the credentials in one write, each key file atomically.
    public static void Rewrite(Repository repository, Func<StoreValue, string> map)
    {
        var store = new CredentialStore(repository);
        List<StoreValue> values = All(repository);
        Dictionary<string, string> rows = store.Rows();
        bool rowsChanged = false;
        foreach (StoreValue value in values)
        {
            string text = map(value);
            if (text == value.Text) continue;
            if (value.FilePath is null)
            {
                rows[value.Name] = text;
                rowsChanged = true;
            }
            else
            {
                Config.AtomicWrite(value.FilePath, Config.Utf8.GetBytes(text));
            }
        }
        if (rowsChanged) store.Save(rows);
    }
}
