using System.Text.Json;

// The repository of a directory: the nearest directory at or above it that holds .git - a directory, or a file in a
// linked worktree or a submodule - and never one past it. Its store is <root>/.mcred-store/, whose layout mcred owns.
internal sealed class Repository
{
    // The newest format this build reads; it still reads and writes format 1 as mcred 1.0.0 does (D20), and format 2 as
    // mcred 2.0.0 does.
    public const int Format = 3;
    // The first format that keeps the credentials as CSV; the formats before it keep them as JSON.
    public const int CsvFormat = 3;
    public const string StoreName = ".mcred-store";
    // The store's name until mcred 2.1.0: a store still there is refused with the move, never read.
    public const string OldStoreName = ".mcred";
    public const string SslIgnoreLine = "/decrypted-ssl-keys/";

    private Repository(string root) => Root = root;

    public string Root { get; }
    public string Store => Path.Combine(Root, StoreName);
    public string OldStore => Path.Combine(Root, OldStoreName);
    public bool HasOldStore => File.Exists(Path.Combine(OldStore, "repository.json"));
    public string CredentialsCsvPath => Path.Combine(Store, "encrypted-credentials.csv");
    public string CredentialsJsonPath => Path.Combine(Store, "encrypted-credentials.json");
    public string EncryptedSshKeys => Path.Combine(Store, "encrypted-ssh-keys");
    public string EncryptedSslKeys => Path.Combine(Store, "encrypted-ssl-keys");
    public string DecryptedSslKeys => Path.Combine(Store, "decrypted-ssl-keys");
    public string MetadataPath => Path.Combine(Store, "repository.json");
    public string GitIgnorePath => Path.Combine(Store, ".gitignore");
    public string GitAttributesPath => Path.Combine(Store, ".gitattributes");

    public static Repository Find(string start) =>
        TryFind(start) ?? throw new InvalidOperationException(
            $"No repository here: no .git in {Path.GetFullPath(start)} or above it. Run mcred inside a repository.");

    public static Repository? TryFind(string start)
    {
        for (string? current = Path.GetFullPath(start); current is not null; current = Path.GetDirectoryName(current))
        {
            string git = Path.Combine(current, ".git");
            if (Directory.Exists(git) || File.Exists(git))
                return new Repository(Path.TrimEndingDirectorySeparator(current));
        }
        return null;
    }

    public static Repository Current() => Find(Environment.CurrentDirectory);

    public string CredentialsPath(int format) => format >= CsvFormat ? CredentialsCsvPath : CredentialsJsonPath;

    // A JSON file beside a CSV store comes from a merged branch or a stopped 'mcred migrate'. Read as nothing, its rows
    // would vanish without a word, so every command but status and migrate refuses it.
    public bool HasOldCredentials(RepositoryMetadata metadata) => metadata.Format >= CsvFormat && File.Exists(CredentialsJsonPath);

    public RepositoryMetadata RequireStore()
    {
        RepositoryMetadata metadata = ReadStore();
        if (HasOldCredentials(metadata))
            throw new InvalidOperationException(
                $"{CredentialsJsonPath} lies beside a format-{metadata.Format} store, and its rows are not read. " +
                "Run 'mcred migrate' to take them into the CSV.");
        return metadata;
    }

    // The store's metadata; a repository without .mcred-store/, or a .mcred-store/ that mcred did not set up, is
    // refused, so a command run in the wrong directory never starts a stray store. Status and 'mcred migrate' start here;
    // every other command starts at RequireStore.
    public RepositoryMetadata ReadStore()
    {
        RefuseOldStore();
        if (!Directory.Exists(Store))
            throw new InvalidOperationException($"{Root} has no {StoreName}/ store. Run 'mcred repo init' there to start one.");
        if (!File.Exists(MetadataPath))
            throw new InvalidOperationException($"{Store} has no repository.json. Run 'mcred repo init' to complete it.");
        return RepositoryMetadata.Load(MetadataPath);
    }

    // mcred repo init: the store's directory and the three files mcred owns; a file that exists is kept as it is. A new
    // metadata file is format 1 here (completing a store); 'repo init <seat>' starts a store in the newest format
    // (Seats.InitStore).
    public IReadOnlyList<string> Init(RepositoryMetadata? metadata = null)
    {
        RefuseOldStore();
        if (File.Exists(MetadataPath)) RequireStore();
        var written = new List<string>();
        Directory.CreateDirectory(Store);
        if (!File.Exists(MetadataPath))
        {
            (metadata ?? RepositoryMetadata.New()).Save(MetadataPath);
            written.Add(MetadataPath);
        }
        if (!File.Exists(GitIgnorePath))
        {
            Config.AtomicWrite(GitIgnorePath, Config.Utf8.GetBytes(
                "# mcred: the clear SSL keys that decrypt-ssl-keys restores, never committed\n" + SslIgnoreLine + "\n"));
            written.Add(GitIgnorePath);
        }
        if (!File.Exists(GitAttributesPath))
        {
            Config.AtomicWrite(GitAttributesPath, Config.Utf8.GetBytes("# mcred: ciphertext, kept byte for byte\n* -text\n"));
            written.Add(GitAttributesPath);
        }
        return written;
    }

    // A store under the old name is moved by its repository (a commit), never read or written by this build, and no
    // second store is started beside it.
    private void RefuseOldStore()
    {
        if (HasOldStore)
            throw new InvalidOperationException(
                $"{Root} keeps its store in {OldStoreName}/, the name before mcred 2.1.0; this mcred reads only {StoreName}/. " +
                $"Move it: git mv {OldStoreName} {StoreName}, then commit.");
    }

    // A clear SSL key is written only while git ignores the clear tree.
    public void RequireSslIgnored()
    {
        bool ignored = File.Exists(GitIgnorePath) && File.ReadAllLines(GitIgnorePath).Any(line => line.Trim() == SslIgnoreLine);
        if (!ignored)
            throw new InvalidOperationException($"{GitIgnorePath} must hold the line {SslIgnoreLine} before a clear SSL key is written. Run 'mcred repo init'.");
    }
}

// A slot: the repository key sealed under one seat's root key ("seat") or under a one-off root key ("one-off").
internal sealed record Slot(string Name, string Kind, string KeyId, string Added, string AddedBy, string? Expires, string Wrapped)
{
    public const string SeatKind = "seat";
    public const string OneOffKind = "one-off";
}

// An earlier repository key, sealed under the current one, so a value written before a rotation still opens.
internal sealed record EarlierKey(string RepositoryKeyId, string Wrapped);

// repository.json. Format 1 (mcred 1.0.0): the format and an id, values v2 under the root key. Format 2 (mcred 2.0.0):
// also the repository key's id, the slots and the earlier keys, values v3 under the repository key. Format 3 (mcred
// 3.0.0): the same fields; only the credentials' file differs (CredentialStore).
internal sealed class RepositoryMetadata
{
    public int Format { get; init; }
    public string Id { get; init; } = "";
    public string RepositoryKeyId { get; init; } = "";
    public List<Slot> Slots { get; init; } = [];
    public List<EarlierKey> EarlierKeys { get; init; } = [];

    public static RepositoryMetadata New() => new() { Format = 1, Id = Guid.NewGuid().ToString("D") };

    public static RepositoryMetadata Load(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(path));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("format", out JsonElement format) || format.ValueKind != JsonValueKind.Number ||
            !root.TryGetProperty("id", out JsonElement id) || id.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{path} must be an object with a numeric format and a string id.");
        int value = format.GetInt32();
        if (value > Repository.Format)
            throw new InvalidOperationException(
                $"The store is format {value}; this mcred ({Config.Version}) reads up to format {Repository.Format}. Update mcred.");
        if (value < 1)
            throw new InvalidDataException($"{path} has an unknown format {value}.");
        if (value == 1)
            return new RepositoryMetadata { Format = 1, Id = id.GetString()! };
        return new RepositoryMetadata
        {
            Format = value,
            Id = id.GetString()!,
            RepositoryKeyId = Text(root, "repositoryKeyId", path),
            Slots = root.GetProperty("slots").EnumerateArray().Select(slot => new Slot(
                Text(slot, "name", path), Text(slot, "kind", path), Text(slot, "keyId", path), Text(slot, "added", path),
                Text(slot, "addedBy", path),
                slot.TryGetProperty("expires", out JsonElement expires) ? expires.GetString() : null,
                Text(slot, "wrapped", path))).ToList(),
            EarlierKeys = root.TryGetProperty("earlierKeys", out JsonElement earlier)
                ? earlier.EnumerateArray().Select(key => new EarlierKey(Text(key, "repositoryKeyId", path), Text(key, "wrapped", path))).ToList()
                : [],
        };
    }

    private static string Text(JsonElement element, string name, string path) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidDataException($"{path}: '{name}' must be a string.");

    // A fixed field order, indented with LF, so a slot's change is a small diff.
    public void Save(string path)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("format", Format);
            writer.WriteString("id", Id);
            if (Format >= 2)
            {
                writer.WriteString("repositoryKeyId", RepositoryKeyId);
                writer.WriteStartArray("slots");
                foreach (Slot slot in Slots)
                {
                    writer.WriteStartObject();
                    writer.WriteString("name", slot.Name);
                    writer.WriteString("kind", slot.Kind);
                    writer.WriteString("keyId", slot.KeyId);
                    writer.WriteString("added", slot.Added);
                    writer.WriteString("addedBy", slot.AddedBy);
                    if (slot.Expires is not null) writer.WriteString("expires", slot.Expires);
                    writer.WriteString("wrapped", slot.Wrapped);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
                writer.WriteStartArray("earlierKeys");
                foreach (EarlierKey key in EarlierKeys)
                {
                    writer.WriteStartObject();
                    writer.WriteString("repositoryKeyId", key.RepositoryKeyId);
                    writer.WriteString("wrapped", key.Wrapped);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        Config.AtomicWrite(path, buffer.ToArray());
    }
}
