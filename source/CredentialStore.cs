using System.Text;
using System.Text.Json;
using System.Security.Cryptography;

// A repository's credentials, in the file the store's format names. Format 3: .mcred-store/encrypted-credentials.csv,
// one name,value line per credential, never quoted, read strictly. Formats 1 and 2: .mcred-store/encrypted-credentials.json,
// one JSON object of name -> ciphertext. The format is read at each use, because 'mcred migrate' changes it.
internal sealed class CredentialStore(Repository repository)
{
    private const string CsvHeader = "name,value";
    private const string NotV3 = "a value that is not v3";
    private static readonly char[] CsvNameForbids = [',', '"'];
    private static readonly char[] CsvValueForbids = [',', '"', '\r', '\n', '\0'];

    public string FilePath => repository.CredentialsPath(Format);

    private int Format => repository.ReadStore().Format;

    public void Encrypt(string key, byte[] plaintext)
    {
        ValidateNewKey(key);
        IValueCipher cipher = Ciphers.Open(repository);
        Dictionary<string, string> rows = Load(allowMissing: true);
        rows[key] = cipher.Encrypt(plaintext, key);
        Save(rows);
    }

    // Every row, as the file holds it; the rewrite of a migration or a rotation saves them back in one write.
    public Dictionary<string, string> Rows() => Load(allowMissing: true);

    public void Save(Dictionary<string, string> rows)
    {
        int format = Format;
        Config.AtomicWrite(repository.CredentialsPath(format), format >= Repository.CsvFormat
            ? CsvBytes(rows)
            : JsonSerializer.SerializeToUtf8Bytes(
                rows.OrderBy(row => row.Key, StringComparer.Ordinal)
                    .ToDictionary(row => row.Key, row => row.Value, StringComparer.Ordinal),
                Config.Json));
    }

    public byte[] Decrypt(string key)
    {
        ValidateKey(key);
        IValueCipher cipher = Ciphers.Open(repository);
        Dictionary<string, string> rows = Load(allowMissing: false);
        if (!rows.TryGetValue(key, out string? value))
            throw new InvalidOperationException($"Credential '{key}' was not found in {FilePath}.");
        return cipher.Decrypt(value, key);
    }

    public int Count() => File.Exists(FilePath) ? Load(allowMissing: false).Count : 0;

    // mcred list: the names only, sorted. Nothing is decrypted, so it needs no root key.
    public IEnumerable<string> Names(string? prefix)
    {
        repository.RequireStore();
        return Load(allowMissing: true).Keys
            .Where(name => prefix is null || name.StartsWith(prefix, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    // mcred rm: one row goes. Nothing is decrypted, so it needs no root key.
    public void Remove(string key)
    {
        repository.RequireStore();
        Dictionary<string, string> rows = Load(allowMissing: false);
        if (!rows.Remove(key))
            throw new InvalidOperationException($"Credential '{key}' was not found in {FilePath}.");
        Save(rows);
    }

    // mcred mv: the name is authenticated with the value, so a rename decrypts and encrypts again, in memory and in one
    // write. An existing name is never replaced.
    public void Move(string oldKey, string newKey)
    {
        ValidateNewKey(newKey);
        IValueCipher cipher = Ciphers.Open(repository);
        Dictionary<string, string> rows = Load(allowMissing: false);
        if (!rows.TryGetValue(oldKey, out string? value))
            throw new InvalidOperationException($"Credential '{oldKey}' was not found in {FilePath}.");
        if (rows.ContainsKey(newKey))
            throw new InvalidOperationException($"Credential '{newKey}' exists already; nothing written.");
        byte[] plaintext = cipher.Decrypt(value, oldKey);
        try
        {
            rows[newKey] = cipher.Encrypt(plaintext, newKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
        rows.Remove(oldKey);
        Save(rows);
    }

    // mcred migrate: format 2 into format 3, or a JSON file beside a format-3 store into its CSV. No value is decrypted,
    // so it needs no root key and every ciphertext stays as it is. Each step leaves a state this method finishes when it
    // runs again: the CSV is written and checked first, then the metadata, and the JSON goes last. A row in both files must
    // be the same. Returns null when there is nothing to do.
    public (int Rows, int FromJson)? MigrateToCsv()
    {
        RepositoryMetadata metadata = repository.ReadStore();
        if (metadata.Format < 2)
            throw new InvalidOperationException($"{repository.Store} is format {metadata.Format}: run 'mcred migrate <seat>' first, then 'mcred migrate'.");
        if (metadata.Format >= Repository.CsvFormat && !File.Exists(repository.CredentialsJsonPath))
            return null;

        string jsonPath = repository.CredentialsJsonPath, csvPath = repository.CredentialsCsvPath;
        Dictionary<string, string> fromJson = File.Exists(jsonPath) ? ParseJson(File.ReadAllBytes(jsonPath)) : new(StringComparer.Ordinal);
        Dictionary<string, string> rows = File.Exists(csvPath) ? ParseCsv(File.ReadAllBytes(csvPath), csvPath) : new(StringComparer.Ordinal);

        var problems = fromJson
            .Select(row => (row.Key, Problem: CsvProblem(row.Key, row.Value)))
            .Where(row => row.Problem is not null)
            .OrderBy(row => row.Key, StringComparer.Ordinal)
            .Select(row => $"'{row.Key}': {row.Problem}")
            .ToList();
        if (metadata.Format < Repository.CsvFormat)
            problems.AddRange(StoreValue.KeyFiles(repository)
                .Where(file => !file.Text.StartsWith("v3.", StringComparison.Ordinal))
                .Select(file => $"'{file.Name}': {NotV3}"));
        if (problems.Count > 0)
            throw new InvalidDataException(
                $"{problems.Count} row(s) cannot go into the CSV; nothing written. {string.Join("; ", problems)}. " +
                (metadata.Format < Repository.CsvFormat
                    ? "Run 'mcred migrate <seat>' for a value that is not v3, and 'mcred mv' for a name."
                    : $"Take them out of {jsonPath}, then encrypt them again under names the CSV accepts."));

        List<string> differing = fromJson
            .Where(row => rows.TryGetValue(row.Key, out string? value) && value != row.Value)
            .Select(row => row.Key)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (differing.Count > 0)
            throw new InvalidDataException(
                $"{differing.Count} credential(s) differ between {Path.GetFileName(jsonPath)} and {Path.GetFileName(csvPath)}; " +
                $"nothing written: {string.Join(", ", differing)}. Keep one ciphertext for each, in both files, then run 'mcred migrate' again.");

        int added = fromJson.Count(row => rows.TryAdd(row.Key, row.Value));
        Config.AtomicWrite(csvPath, CsvBytes(rows));
        Dictionary<string, string> written = ParseCsv(File.ReadAllBytes(csvPath), csvPath);
        if (written.Count != rows.Count || rows.Any(row => !written.TryGetValue(row.Key, out string? value) || value != row.Value))
            throw new InvalidDataException($"{csvPath} did not read back as written; {jsonPath} is kept.");

        if (metadata.Format < Repository.CsvFormat)
            new RepositoryMetadata
            {
                Format = Repository.CsvFormat, Id = metadata.Id, RepositoryKeyId = metadata.RepositoryKeyId,
                Slots = metadata.Slots, EarlierKeys = metadata.EarlierKeys,
            }.Save(repository.MetadataPath);
        File.Delete(jsonPath);
        return (rows.Count, added);
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.IndexOfAny(['\r', '\n', '\0']) >= 0)
            throw new ArgumentException("Key must be nonempty and contain no newline or NUL characters.");
    }

    // A name that is about to be written: the rule of the file it goes into.
    private void ValidateNewKey(string key)
    {
        ValidateKey(key);
        if (Format >= Repository.CsvFormat && key.IndexOfAny(CsvNameForbids) >= 0)
            throw new ArgumentException($"A credential's name may not hold a comma or a double quote in format {Repository.CsvFormat}: '{key}'.");
    }

    private Dictionary<string, string> Load(bool allowMissing)
    {
        int format = Format;
        string path = repository.CredentialsPath(format);
        if (!File.Exists(path))
        {
            if (allowMissing) return new Dictionary<string, string>(StringComparer.Ordinal);
            throw new FileNotFoundException($"Encrypted credentials file was not found: {path}");
        }
        byte[] bytes = File.ReadAllBytes(path);
        return format >= Repository.CsvFormat ? ParseCsv(bytes, path) : ParseJson(bytes);
    }

    private static Dictionary<string, string> ParseJson(byte[] bytes)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        using JsonDocument document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Encrypted credentials JSON must be an object.");
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
        {
            ValidateKey(property.Name);
            if (property.Value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Credential '{property.Name}' must have a string value.");
            if (!rows.TryAdd(property.Name, property.Value.GetString()!))
                throw new InvalidDataException($"Duplicate credential key: {property.Name}");
        }
        return rows;
    }

    // Why a row cannot be a line of the CSV, or null. Every row splits at its one comma, so nothing is ever quoted: a name
    // may not hold a comma or a quote, and a value (Base64, dots and hex) never does.
    private static string? CsvProblem(string name, string value)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(['\r', '\n', '\0']) >= 0)
            return "an empty name, or one with a newline or NUL";
        if (name.IndexOfAny(CsvNameForbids) >= 0)
            return "a name that holds a comma or a double quote";
        if (!value.StartsWith("v3.", StringComparison.Ordinal))
            return NotV3;
        if (value.IndexOfAny(CsvValueForbids) >= 0)
            return "a value that holds a comma, a double quote, a newline or NUL";
        return null;
    }

    // Sorted by name, LF on every OS, a final newline, no byte order mark: adding a row touches one line.
    private static byte[] CsvBytes(Dictionary<string, string> rows)
    {
        var text = new StringBuilder(CsvHeader).Append('\n');
        foreach ((string name, string value) in rows.OrderBy(row => row.Key, StringComparer.Ordinal))
        {
            string? problem = CsvProblem(name, value);
            if (problem is not null)
                throw new InvalidDataException($"Credential '{name}' cannot go into the CSV: {problem}.");
            text.Append(name).Append(',').Append(value).Append('\n');
        }
        return Config.Utf8.GetBytes(text.ToString());
    }

    // Strict, because an editor or a merge may have touched the file: anything but what CsvBytes writes is refused, naming
    // the line, before a write can follow. Only the order of the rows and the final newline may differ, and the next write
    // puts them back. A line's content is never printed: only a well-formed row's name.
    private static Dictionary<string, string> ParseCsv(byte[] bytes, string path)
    {
        InvalidDataException Refuse(int line, string what) => new($"{path}, line {line}: {what}. Nothing written.");

        if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
            throw Refuse(1, "a byte order mark");
        string text;
        try
        {
            text = Config.Utf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new InvalidDataException($"{path} is not UTF-8. Nothing written.");
        }
        string[] lines = text.Split('\n');
        int count = lines[^1].Length == 0 ? lines.Length - 1 : lines.Length;
        if (count == 0) throw Refuse(1, $"the header must be exactly {CsvHeader}");

        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            string line = lines[index];
            int number = index + 1;
            if (line.Contains('\r'))
                throw Refuse(number, "a carriage return (an editor saved CRLF: save it with LF line ends)");
            if (index == 0)
            {
                if (line != CsvHeader) throw Refuse(number, $"the header must be exactly {CsvHeader}");
                continue;
            }
            int comma = line.IndexOf(',');
            if (comma < 0 || line.IndexOf(',', comma + 1) >= 0)
                throw Refuse(number, "not exactly one comma (a git conflict marker, or a row broken by hand)");
            string name = line[..comma], value = line[(comma + 1)..];
            string? problem = CsvProblem(name, value);
            if (problem is not null) throw Refuse(number, problem);
            if (!rows.TryAdd(name, value)) throw Refuse(number, $"a duplicate name '{name}'");
        }
        return rows;
    }

    // The plaintext of a value is never kept longer than its write.
    public static void Zero(byte[] plaintext) => CryptographicOperations.ZeroMemory(plaintext);
}
