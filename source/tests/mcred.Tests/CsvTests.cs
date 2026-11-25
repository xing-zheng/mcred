using System.Security.Cryptography;
using System.Text;
using Xunit;

// Format 3: the credentials as CSV, 'mcred migrate' from format 2, a JSON file beside a format-3 store, and the commands
// that work on both formats (list, rm, mv).
public class CsvTests
{
    // A new store starts in format 3; a format-2 store is a format-1 store after 'mcred migrate <seat>'.
    private static Scratch StoreIn(int format)
    {
        if (format == 3)
        {
            var scratch = new Scratch(withStore: false);
            Seats.InitStore(scratch.Repository, "owner");
            return scratch;
        }
        var older = new Scratch();
        Seats.Migrate(older.Repository, "owner");
        return older;
    }

    private static void UseRootKey(byte[]? key) => RootKeyStore.Current = new MemoryRootKeyStore(key);

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static string Decrypt(Scratch scratch, string name) =>
        Encoding.UTF8.GetString(new CredentialStore(scratch.Repository).Decrypt(name));

    private static void Encrypt(Scratch scratch, string name, string value) =>
        new CredentialStore(scratch.Repository).Encrypt(name, Bytes(value));

    private static int FormatOf(Scratch scratch) => scratch.Repository.ReadStore().Format;

    private static string StatusOf(Scratch scratch)
    {
        TextWriter previous = Console.Out;
        var output = new StringWriter();
        Console.SetOut(output);
        try { Status.Print(scratch.Repository); }
        finally { Console.SetOut(previous); }
        return output.ToString();
    }

    // --- The file (D1)

    [Fact]
    public void ANewStoreWritesTheCsvExactly()
    {
        using Scratch scratch = StoreIn(3);
        var store = new CredentialStore(scratch.Repository);
        store.Encrypt("b/name", Bytes("two"));
        store.Encrypt("a/name", Bytes("one"));
        Assert.Equal(3, FormatOf(scratch));
        Assert.Equal(scratch.Repository.CredentialsCsvPath, store.FilePath);
        Assert.False(File.Exists(scratch.Repository.CredentialsJsonPath));
        Dictionary<string, string> rows = store.Rows();
        Assert.Equal(Bytes($"name,value\na/name,{rows["a/name"]}\nb/name,{rows["b/name"]}\n"), File.ReadAllBytes(store.FilePath));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ACredentialRoundTripsInItsFormatsOwnFile(int format)
    {
        using Scratch scratch = StoreIn(format);
        var store = new CredentialStore(scratch.Repository);
        store.Encrypt("a/name", Bytes("value+with/plus\né"));
        Assert.Equal(format, FormatOf(scratch));
        Assert.Equal("value+with/plus\né", Decrypt(scratch, "a/name"));
        Assert.StartsWith("v3.", store.Rows()["a/name"]);
        Assert.Equal(1, store.Count());
        string own = format == 3 ? scratch.Repository.CredentialsCsvPath : scratch.Repository.CredentialsJsonPath;
        string other = format == 3 ? scratch.Repository.CredentialsJsonPath : scratch.Repository.CredentialsCsvPath;
        Assert.Equal(own, store.FilePath);
        Assert.True(File.Exists(own));
        Assert.False(File.Exists(other));
        Assert.DoesNotContain("\r", File.ReadAllText(own));
    }

    [Fact]
    public void AnExistingNameIsReplacedOnItsLine()
    {
        using Scratch scratch = StoreIn(3);
        Encrypt(scratch, "a", "one");
        Encrypt(scratch, "a", "two");
        Assert.Equal("two", Decrypt(scratch, "a"));
        Assert.Equal(2, File.ReadAllLines(scratch.Repository.CredentialsCsvPath).Length);
    }

    // --- The names (D2)

    [Theory]
    [InlineData("a,b")]
    [InlineData("a\"b")]
    public void ANameWithACommaOrAQuoteIsRefusedInFormat3AndStaysAllowedInFormat2(string name)
    {
        using (Scratch scratch = StoreIn(3))
        {
            var error = Assert.Throws<ArgumentException>(() => Encrypt(scratch, name, "value"));
            Assert.Contains("comma", error.Message);
            Assert.False(File.Exists(scratch.Repository.CredentialsCsvPath));
        }
        using (Scratch scratch = StoreIn(2))
        {
            Encrypt(scratch, name, "value");
            Assert.Equal("value", Decrypt(scratch, name));
        }
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData(" ")]
    public void ANameWithANewlineOrNothingInItIsRefusedInBothFormats(string name)
    {
        foreach (int format in new[] { 2, 3 })
        {
            using Scratch scratch = StoreIn(format);
            Assert.Throws<ArgumentException>(() => Encrypt(scratch, name, "value"));
        }
    }

    // --- Reading is strict (D3): {V} is a good value of the store.

    [Theory]
    [InlineData("﻿name,value\n", 1, "byte order mark")]
    [InlineData("name,value\r\na,{V}\r\n", 1, "carriage return")]
    [InlineData("name,value\na,{V}\r\n", 2, "carriage return")]
    [InlineData("name,value\nb,{V}\na,{V}\r\n", 3, "carriage return")]
    [InlineData("", 1, "header")]
    [InlineData("Name,Value\na,{V}\n", 1, "header")]
    [InlineData("name,value,extra\n", 1, "header")]
    [InlineData("a,{V}\n", 1, "header")]
    [InlineData("name,value\na\n", 2, "one comma")]
    [InlineData("name,value\na,b,{V}\n", 2, "one comma")]
    [InlineData("name,value\n\na,{V}\n", 2, "one comma")]
    [InlineData("name,value\na,{V}\n\n", 3, "one comma")]
    [InlineData("name,value\n<<<<<<< HEAD\na,{V}\n", 2, "one comma")]
    [InlineData("name,value\na,{V}\n=======\n", 3, "one comma")]
    [InlineData("name,value\na,{V}\n>>>>>>> branch\n", 3, "one comma")]
    [InlineData("name,value\na,{V}\na,{V}\n", 3, "duplicate")]
    [InlineData("name,value\na,v2.AAAA.AAAA.AAAA.AAAA\n", 2, "not v3")]
    [InlineData("name,value\na,\n", 2, "not v3")]
    [InlineData("name,value\na,\"{V}\"\n", 2, "not v3")]
    [InlineData("name,value\n\"a\",{V}\n", 2, "double quote")]
    [InlineData("name,value\na,{V}\"\n", 2, "double quote")]
    [InlineData("name,value\n,{V}\n", 2, "empty name")]
    public void AnythingButTheFormatIsRefusedNamingTheLineAndNothingIsWritten(string content, int line, string reason)
    {
        using Scratch scratch = StoreIn(3);
        var store = new CredentialStore(scratch.Repository);
        store.Encrypt("a", Bytes("value"));
        string good = store.Rows()["a"];
        string path = scratch.Repository.CredentialsCsvPath;
        byte[] bytes = new UTF8Encoding(false).GetBytes(content.Replace("{V}", good));
        File.WriteAllBytes(path, bytes);

        foreach (Action command in new Action[]
        {
            () => store.Decrypt("a"),
            () => store.Encrypt("b", Bytes("value")),
            () => store.Remove("a"),
            () => store.Move("a", "b"),
            () => store.Names(null),
            () => store.Rows(),
        })
        {
            var error = Assert.Throws<InvalidDataException>(command);
            Assert.Contains($"line {line}:", error.Message);
            Assert.Contains(reason, error.Message);
            Assert.DoesNotContain(good, error.Message);
        }
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void RowsOutOfOrderAndAMissingFinalNewlineAreAcceptedAndTheNextWritePutsThemRight()
    {
        using Scratch scratch = StoreIn(3);
        var store = new CredentialStore(scratch.Repository);
        store.Encrypt("a", Bytes("one"));
        store.Encrypt("b", Bytes("two"));
        Dictionary<string, string> rows = store.Rows();
        string path = scratch.Repository.CredentialsCsvPath;
        File.WriteAllBytes(path, Bytes($"name,value\nb,{rows["b"]}\na,{rows["a"]}"));
        Assert.Equal("one", Decrypt(scratch, "a"));
        Assert.Equal("two", Decrypt(scratch, "b"));

        store.Encrypt("c", Bytes("three"));
        Assert.Equal(Bytes($"name,value\na,{rows["a"]}\nb,{rows["b"]}\nc,{store.Rows()["c"]}\n"), File.ReadAllBytes(path));
    }

    [Fact]
    public void AWriteOfARowTheCsvWouldRefuseIsRefused()
    {
        using Scratch scratch = StoreIn(3);
        var store = new CredentialStore(scratch.Repository);
        Assert.Throws<InvalidDataException>(() => store.Save(new Dictionary<string, string> { ["a"] = "v2.AAAA" }));
        Assert.Throws<InvalidDataException>(() => store.Save(new Dictionary<string, string> { ["a,b"] = "v3.AAAA" }));
        Assert.False(File.Exists(scratch.Repository.CredentialsCsvPath));
    }

    // --- mcred migrate (D5)

    // A format-2 store with two credentials and a key file.
    private static Scratch Format2Store()
    {
        Scratch scratch = StoreIn(2);
        Encrypt(scratch, "b/name", "two");
        Encrypt(scratch, "a/name", "one");
        KeyStore.Add(scratch.Repository, KeyStoreKind.Ssh, scratch.Write("src/k.pem", "KEY"), "cp", replace: false);
        return scratch;
    }

    [Fact]
    public void MigrateKeepsEveryCiphertextNeedsNoRootKeyAndDeletesTheJson()
    {
        using Scratch scratch = Format2Store();
        Repository repository = scratch.Repository;
        var store = new CredentialStore(repository);
        Dictionary<string, string> before = store.Rows();
        string metadataBefore = File.ReadAllText(repository.MetadataPath);
        string keyFile = Path.Combine(repository.EncryptedSshKeys, "cp", "k.pem");
        byte[] keyFileBefore = File.ReadAllBytes(keyFile);

        UseRootKey(null);
        Assert.Equal<(int, int)?>((2, 2), store.MigrateToCsv());

        Assert.Equal(3, FormatOf(scratch));
        Assert.False(File.Exists(repository.CredentialsJsonPath));
        Assert.Equal(before, store.Rows());
        Assert.Equal(metadataBefore.Replace("\"format\": 2", "\"format\": 3"), File.ReadAllText(repository.MetadataPath));
        Assert.Equal(keyFileBefore, File.ReadAllBytes(keyFile));
        Assert.Equal(Bytes($"name,value\na/name,{before["a/name"]}\nb/name,{before["b/name"]}\n"), File.ReadAllBytes(store.FilePath));

        UseRootKey(scratch.RootKey);
        Assert.Equal("one", Decrypt(scratch, "a/name"));
        Assert.Equal("two", Decrypt(scratch, "b/name"));
        Assert.Equal((0, 1), KeyStore.DecryptAll(repository, KeyStoreKind.Ssh, replace: false));
        Encrypt(scratch, "c", "three");
        Assert.Equal("three", Decrypt(scratch, "c"));
        Assert.False(File.Exists(repository.CredentialsJsonPath));
    }

    [Fact]
    public void MigrateOfAFormat3StoreHasNothingToDo()
    {
        using Scratch scratch = StoreIn(3);
        Encrypt(scratch, "a", "value");
        byte[] before = File.ReadAllBytes(scratch.Repository.CredentialsCsvPath);
        Assert.Null(new CredentialStore(scratch.Repository).MigrateToCsv());
        Assert.Equal(before, File.ReadAllBytes(scratch.Repository.CredentialsCsvPath));
    }

    [Fact]
    public void MigrateOfAStoreWithoutCredentialsWritesTheHeader()
    {
        using Scratch scratch = StoreIn(2);
        Assert.False(File.Exists(scratch.Repository.CredentialsJsonPath));
        Assert.Equal<(int, int)?>((0, 0), new CredentialStore(scratch.Repository).MigrateToCsv());
        Assert.Equal(3, FormatOf(scratch));
        Assert.Equal(Bytes("name,value\n"), File.ReadAllBytes(scratch.Repository.CredentialsCsvPath));
    }

    [Fact]
    public void MigrateRefusesAFormat1StoreAndNamesTheSeatsMigrate()
    {
        using var scratch = new Scratch();
        Encrypt(scratch, "a", "value");
        var error = Assert.Throws<InvalidOperationException>(() => new CredentialStore(scratch.Repository).MigrateToCsv());
        Assert.Contains("mcred migrate <seat>", error.Message);
        Assert.Equal(1, FormatOf(scratch));
        Assert.False(File.Exists(scratch.Repository.CredentialsCsvPath));
    }

    [Fact]
    public void MigrateRefusesAV2ValueAndANameThatBreaksTheCsvNamingEachAndWritesNothing()
    {
        using Scratch scratch = StoreIn(2);
        Repository repository = scratch.Repository;
        var store = new CredentialStore(repository);
        Encrypt(scratch, "fine", "value");
        Encrypt(scratch, "with,comma", "value");
        Encrypt(scratch, "with\"quote", "value");
        Dictionary<string, string> rows = store.Rows();
        rows["still-v2"] = "v2.AAAA.AAAA.AAAA.AAAA";
        store.Save(rows);
        scratch.Write(Path.Combine(".mcred-store", "encrypted-ssh-keys", "old.pem"), "v2.AAAA.AAAA.AAAA.AAAA");
        byte[] json = File.ReadAllBytes(repository.CredentialsJsonPath);

        var error = Assert.Throws<InvalidDataException>(() => store.MigrateToCsv());
        foreach (string named in new[] { "with,comma", "with\"quote", "still-v2", "key-file:old.pem", "mcred migrate <seat>" })
            Assert.Contains(named, error.Message);
        Assert.DoesNotContain("'fine'", error.Message);
        Assert.Equal(2, FormatOf(scratch));
        Assert.Equal(json, File.ReadAllBytes(repository.CredentialsJsonPath));
        Assert.False(File.Exists(repository.CredentialsCsvPath));
    }

    // Stopped after: 1, the checks (nothing written); 2, the CSV; 3, the metadata; 4, the JSON's deletion (all done).
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void AMigrateThatStoppedIsFinishedByRunningItAgain(int stoppedAfter)
    {
        using Scratch scratch = Format2Store();
        Repository repository = scratch.Repository;
        var store = new CredentialStore(repository);
        byte[] oldJson = File.ReadAllBytes(repository.CredentialsJsonPath);
        byte[] oldMetadata = File.ReadAllBytes(repository.MetadataPath);
        store.MigrateToCsv();
        byte[] csv = File.ReadAllBytes(repository.CredentialsCsvPath);
        byte[] metadata = File.ReadAllBytes(repository.MetadataPath);

        // Put back what the run had not reached.
        if (stoppedAfter < 2) File.Delete(repository.CredentialsCsvPath);
        if (stoppedAfter < 3) File.WriteAllBytes(repository.MetadataPath, oldMetadata);
        if (stoppedAfter < 4) File.WriteAllBytes(repository.CredentialsJsonPath, oldJson);

        (int, int)? result = store.MigrateToCsv();
        Assert.Equal(stoppedAfter == 4, result is null);
        Assert.Equal(csv, File.ReadAllBytes(repository.CredentialsCsvPath));
        Assert.Equal(metadata, File.ReadAllBytes(repository.MetadataPath));
        Assert.False(File.Exists(repository.CredentialsJsonPath));
        Assert.Equal("one", Decrypt(scratch, "a/name"));
        Assert.Equal((0, 1), KeyStore.DecryptAll(repository, KeyStoreKind.Ssh, replace: false));
    }

    [Fact]
    public void AStoppedMigrateThenAWriteInFormat2ListsTheRowThatChanged()
    {
        using Scratch scratch = Format2Store();
        Repository repository = scratch.Repository;
        var store = new CredentialStore(repository);
        byte[] oldJson = File.ReadAllBytes(repository.CredentialsJsonPath);
        byte[] oldMetadata = File.ReadAllBytes(repository.MetadataPath);
        store.MigrateToCsv();
        File.WriteAllBytes(repository.MetadataPath, oldMetadata);
        File.WriteAllBytes(repository.CredentialsJsonPath, oldJson);

        Encrypt(scratch, "a/name", "changed");
        var error = Assert.Throws<InvalidDataException>(() => store.MigrateToCsv());
        Assert.Contains("a/name", error.Message);
        Assert.DoesNotContain("b/name", error.Message);
        Assert.Equal(2, FormatOf(scratch));

        File.Delete(repository.CredentialsCsvPath);
        store.MigrateToCsv();
        Assert.Equal("changed", Decrypt(scratch, "a/name"));
    }

    // --- A JSON file beside a format-3 store (D6)

    // A format-3 store whose CSV holds a and b, and a JSON file holding what the test gives it.
    private static Scratch WithJsonBeside(Func<Dictionary<string, string>, Dictionary<string, string>> json)
    {
        Scratch scratch = StoreIn(3);
        Encrypt(scratch, "a", "one");
        Encrypt(scratch, "b", "two");
        File.WriteAllBytes(scratch.Repository.CredentialsJsonPath,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(json(new CredentialStore(scratch.Repository).Rows())));
        return scratch;
    }

    [Fact]
    public void AJsonFileBesideAFormat3StoreIsRefusedByEveryCommandButStatusAndMigrate()
    {
        using Scratch scratch = WithJsonBeside(rows => rows);
        Repository repository = scratch.Repository;
        var store = new CredentialStore(repository);
        byte[] csv = File.ReadAllBytes(repository.CredentialsCsvPath);
        byte[] json = File.ReadAllBytes(repository.CredentialsJsonPath);
        byte[] metadata = File.ReadAllBytes(repository.MetadataPath);
        string key = scratch.Write("src/k.pem", "KEY");

        foreach (Action command in new Action[]
        {
            () => store.Decrypt("a"),
            () => store.Encrypt("c", Bytes("value")),
            () => store.Names(null),
            () => store.Remove("a"),
            () => store.Move("a", "c"),
            () => Seats.Invite(repository, "alice", 72),
            () => Seats.CheckInvite(repository, "alice"),
            () => Seats.Join(repository, "alice", Crypto.ToCode(new byte[16])),
            () => Seats.Remove(repository, "owner"),
            () => Seats.Rotate(repository),
            () => Seats.List(repository).ToList(),
            () => Seats.Migrate(repository, "owner"),
            () => KeyStore.Add(repository, KeyStoreKind.Ssh, key, null, replace: false),
            () => KeyStore.DecryptAll(repository, KeyStoreKind.Ssh, replace: false),
            () => repository.Init(),
        })
        {
            var error = Assert.Throws<InvalidOperationException>(command);
            Assert.Contains("mcred migrate", error.Message);
            Assert.Contains(repository.CredentialsJsonPath, error.Message);
        }
        Assert.Equal(csv, File.ReadAllBytes(repository.CredentialsCsvPath));
        Assert.Equal(json, File.ReadAllBytes(repository.CredentialsJsonPath));
        Assert.Equal(metadata, File.ReadAllBytes(repository.MetadataPath));

        UseRootKey(null);
        var init = Assert.Throws<InvalidOperationException>(() => Seats.InitExisting(scratch.Root, () => "a long enough test passphrase"));
        Assert.Contains("mcred migrate", init.Message);
    }

    [Fact]
    public void StatusNamesTheJsonFileAndCountsTheCsv()
    {
        using Scratch scratch = WithJsonBeside(rows =>
        {
            rows["only-in-json"] = rows["a"];
            return rows;
        });
        string status = StatusOf(scratch);
        Assert.Contains($"old_credentials={scratch.Repository.CredentialsJsonPath}", status);
        Assert.Contains("format=3", status);
        Assert.Contains("credentials=2", status);
        Assert.Contains("seat=owner", status);
    }

    [Fact]
    public void StatusNamesNoJsonFileWhenThereIsNone()
    {
        using (Scratch scratch = StoreIn(3))
        {
            Encrypt(scratch, "a", "value");
            string status = StatusOf(scratch);
            Assert.DoesNotContain("old_credentials=", status);
            Assert.Contains("format=3", status);
            Assert.Contains("credentials=1", status);
        }
        using (Scratch scratch = StoreIn(2))
        {
            Encrypt(scratch, "a", "value");
            string status = StatusOf(scratch);
            Assert.DoesNotContain("old_credentials=", status);
            Assert.Contains("format=2", status);
            Assert.Contains("credentials=1", status);
        }
    }

    [Fact]
    public void MigrateAddsARowOnlyInTheJsonAndDropsAnIdenticalOne()
    {
        using Scratch scratch = StoreIn(3);
        var store = new CredentialStore(scratch.Repository);
        Encrypt(scratch, "a", "one");
        Encrypt(scratch, "b", "two");
        Encrypt(scratch, "from-branch", "branch value");

        // The JSON holds all three, as a branch's would; the CSV does not have the one the branch wrote.
        Dictionary<string, string> json = store.Rows();
        File.WriteAllBytes(scratch.Repository.CredentialsJsonPath, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(json));
        File.WriteAllBytes(store.FilePath, Bytes($"name,value\na,{json["a"]}\nb,{json["b"]}\n"));

        UseRootKey(null);
        Assert.Equal<(int, int)?>((3, 1), store.MigrateToCsv());
        UseRootKey(scratch.RootKey);

        Assert.False(File.Exists(scratch.Repository.CredentialsJsonPath));
        Assert.Equal(json, store.Rows());
        Assert.Equal("branch value", Decrypt(scratch, "from-branch"));
        Assert.Equal("one", Decrypt(scratch, "a"));
        Assert.DoesNotContain("old_credentials=", StatusOf(scratch));
    }

    [Fact]
    public void MigrateKeepsARowOnlyInTheCsv()
    {
        using Scratch scratch = WithJsonBeside(rows =>
        {
            rows.Remove("b");
            return rows;
        });
        var store = new CredentialStore(scratch.Repository);
        Assert.Equal<(int, int)?>((2, 0), store.MigrateToCsv());
        Assert.Equal("two", Decrypt(scratch, "b"));
    }

    [Fact]
    public void MigrateListsARowThatDiffersAndWritesNothing()
    {
        using Scratch scratch = WithJsonBeside(rows =>
        {
            rows["b"] = rows["a"];
            return rows;
        });
        Repository repository = scratch.Repository;
        byte[] csv = File.ReadAllBytes(repository.CredentialsCsvPath);
        byte[] json = File.ReadAllBytes(repository.CredentialsJsonPath);
        var error = Assert.Throws<InvalidDataException>(() => new CredentialStore(repository).MigrateToCsv());
        Assert.Contains(": b.", error.Message);
        Assert.Equal(csv, File.ReadAllBytes(repository.CredentialsCsvPath));
        Assert.Equal(json, File.ReadAllBytes(repository.CredentialsJsonPath));
        Assert.Contains("old_credentials=", StatusOf(scratch));
    }

    [Fact]
    public void MigrateRefusesARowOfTheJsonTheCsvCannotHoldAndWritesNothing()
    {
        using Scratch scratch = WithJsonBeside(rows =>
        {
            rows["with,comma"] = rows["a"];
            rows["still-v2"] = "v2.AAAA.AAAA.AAAA.AAAA";
            return rows;
        });
        Repository repository = scratch.Repository;
        byte[] csv = File.ReadAllBytes(repository.CredentialsCsvPath);
        var error = Assert.Throws<InvalidDataException>(() => new CredentialStore(repository).MigrateToCsv());
        Assert.Contains("with,comma", error.Message);
        Assert.Contains("still-v2", error.Message);
        Assert.Contains(repository.CredentialsJsonPath, error.Message);
        Assert.DoesNotContain("<seat>", error.Message);   // the seat's migrate refuses this store too
        Assert.Equal(csv, File.ReadAllBytes(repository.CredentialsCsvPath));
        Assert.True(File.Exists(repository.CredentialsJsonPath));
    }

    [Fact]
    public void MigrateLeavesAConflictedCsvAndItsJsonAlone()
    {
        using Scratch scratch = WithJsonBeside(rows => rows);
        Repository repository = scratch.Repository;
        File.AppendAllText(repository.CredentialsCsvPath, "<<<<<<< HEAD\n");
        byte[] csv = File.ReadAllBytes(repository.CredentialsCsvPath);
        var error = Assert.Throws<InvalidDataException>(() => new CredentialStore(repository).MigrateToCsv());
        Assert.Contains("line 4:", error.Message);
        Assert.Equal(csv, File.ReadAllBytes(repository.CredentialsCsvPath));
        Assert.True(File.Exists(repository.CredentialsJsonPath));
    }

    [Fact]
    public void AJsonFileBesideAFormat2StoreIsItsOwnFile()
    {
        using Scratch scratch = StoreIn(2);
        Encrypt(scratch, "a", "value");
        Assert.Equal("value", Decrypt(scratch, "a"));
        Assert.DoesNotContain("old_credentials=", StatusOf(scratch));
    }

    // --- The other commands keep the format (D4)

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void RotateInviteJoinAndRemoveKeepTheFormat(int format)
    {
        using Scratch scratch = StoreIn(format);
        Repository repository = scratch.Repository;
        Encrypt(scratch, "a", "value");
        string code = Seats.Invite(repository, "alice", 72);
        Assert.Equal(format, FormatOf(scratch));

        byte[] aliceKey = RandomNumberGenerator.GetBytes(32);
        UseRootKey(aliceKey);
        Seats.Join(repository, "alice", code);
        Assert.Equal(format, FormatOf(scratch));
        Assert.Equal("value", Decrypt(scratch, "a"));
        Encrypt(scratch, "from-alice", "her value");

        UseRootKey(scratch.RootKey);
        Assert.Equal("her value", Decrypt(scratch, "from-alice"));
        Seats.Invite(repository, "bob", 72);
        Seats.Remove(repository, "bob");
        Assert.Equal(format, FormatOf(scratch));

        string before = new CredentialStore(repository).Rows()["a"];
        (int count, IReadOnlyList<string> dropped) = Seats.Rotate(repository);
        Assert.Equal(2, count);
        Assert.Equal(["alice"], dropped);
        Assert.Equal(format, FormatOf(scratch));
        Assert.NotEqual(before, new CredentialStore(repository).Rows()["a"]);
        Assert.Equal("value", Decrypt(scratch, "a"));
        Assert.Equal("her value", Decrypt(scratch, "from-alice"));
        string other = format == 3 ? repository.CredentialsJsonPath : repository.CredentialsCsvPath;
        Assert.False(File.Exists(other));
    }

    [Fact]
    public void AValueWrittenBeforeARotationStillOpensInACsvStore()
    {
        using Scratch scratch = StoreIn(3);
        var store = new CredentialStore(scratch.Repository);
        store.Encrypt("a", Bytes("value"));
        string beforeRotation = store.Rows()["a"];
        Seats.Rotate(scratch.Repository);
        Dictionary<string, string> rows = store.Rows();
        rows["a"] = beforeRotation;
        store.Save(rows);
        Assert.Equal("value", Decrypt(scratch, "a"));
        Assert.Contains("values_earlier_key=1", StatusOf(scratch));
    }

    [Fact]
    public void TheSeatsMigrateLeavesAFormat3StoreAlone()
    {
        using Scratch scratch = StoreIn(3);
        Encrypt(scratch, "a", "value");
        byte[] csv = File.ReadAllBytes(scratch.Repository.CredentialsCsvPath);
        Assert.Equal((0, 1), Seats.Migrate(scratch.Repository, "owner"));
        Assert.Equal(3, FormatOf(scratch));
        Assert.Equal(csv, File.ReadAllBytes(scratch.Repository.CredentialsCsvPath));
    }

    [Fact]
    public void TheSeatsMigrateStillTakesFormat1ToFormat2AndNoFurther()
    {
        using var scratch = new Scratch();
        Encrypt(scratch, "a", "value");
        Seats.Migrate(scratch.Repository, "owner");
        Assert.Equal(2, FormatOf(scratch));
        Assert.True(File.Exists(scratch.Repository.CredentialsJsonPath));
        Assert.False(File.Exists(scratch.Repository.CredentialsCsvPath));

        new CredentialStore(scratch.Repository).MigrateToCsv();
        Assert.Equal(3, FormatOf(scratch));
        Assert.Equal("value", Decrypt(scratch, "a"));
    }

    // --- list, rm and mv (D9)

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void ListPrintsTheNamesSortedAndNeedsNoRootKey(int format)
    {
        using Scratch scratch = StoreIn(format);
        var store = new CredentialStore(scratch.Repository);
        Assert.Empty(store.Names(null));
        Encrypt(scratch, "b/two", "value");
        Encrypt(scratch, "a/one", "value");
        Encrypt(scratch, "b/one", "value");
        Encrypt(scratch, "B/upper", "value");

        UseRootKey(null);
        Assert.Equal(["B/upper", "a/one", "b/one", "b/two"], store.Names(null));
        Assert.Equal(["b/one", "b/two"], store.Names("b/"));
        Assert.Empty(store.Names("nothing"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void RmRemovesOneRowAndNeedsNoRootKey(int format)
    {
        using Scratch scratch = StoreIn(format);
        var store = new CredentialStore(scratch.Repository);
        Encrypt(scratch, "a", "one");
        Encrypt(scratch, "b", "two");

        UseRootKey(null);
        store.Remove("a");
        Assert.Equal(["b"], store.Names(null));
        var error = Assert.Throws<InvalidOperationException>(() => store.Remove("a"));
        Assert.Contains("was not found", error.Message);

        UseRootKey(scratch.RootKey);
        Assert.Equal("two", Decrypt(scratch, "b"));
        Assert.Throws<InvalidOperationException>(() => Decrypt(scratch, "a"));
    }

    [Fact]
    public void RmOfTheLastRowLeavesTheHeader()
    {
        using Scratch scratch = StoreIn(3);
        var store = new CredentialStore(scratch.Repository);
        Encrypt(scratch, "a", "one");
        store.Remove("a");
        Assert.Equal(Bytes("name,value\n"), File.ReadAllBytes(store.FilePath));
        Assert.Equal(0, store.Count());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void MvEncryptsAgainUnderTheNewNameInOneWrite(int format)
    {
        using Scratch scratch = StoreIn(format);
        var store = new CredentialStore(scratch.Repository);
        Encrypt(scratch, "old", "value");
        Encrypt(scratch, "other", "other value");
        string before = store.Rows()["old"];

        store.Move("old", "new");

        Assert.Equal(["new", "other"], store.Names(null));
        Assert.Equal("value", Decrypt(scratch, "new"));
        Assert.NotEqual(before, store.Rows()["new"]);
        Assert.Equal("other value", Decrypt(scratch, "other"));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void MvRefusesAMissingOldNameAndAnExistingNewNameAndWritesNothing(int format)
    {
        using Scratch scratch = StoreIn(format);
        var store = new CredentialStore(scratch.Repository);
        Encrypt(scratch, "a", "one");
        Encrypt(scratch, "b", "two");
        byte[] before = File.ReadAllBytes(store.FilePath);

        Assert.Contains("was not found", Assert.Throws<InvalidOperationException>(() => store.Move("missing", "c")).Message);
        Assert.Contains("exists already", Assert.Throws<InvalidOperationException>(() => store.Move("a", "b")).Message);
        Assert.Contains("exists already", Assert.Throws<InvalidOperationException>(() => store.Move("a", "a")).Message);
        Assert.Throws<ArgumentException>(() => store.Move("a", " "));
        Assert.Equal(before, File.ReadAllBytes(store.FilePath));
    }

    [Fact]
    public void MvToANameTheCsvRefusesWritesNothingAndMvOutOfOneWorksInFormat2()
    {
        using (Scratch scratch = StoreIn(3))
        {
            var store = new CredentialStore(scratch.Repository);
            Encrypt(scratch, "a", "one");
            byte[] before = File.ReadAllBytes(store.FilePath);
            Assert.Throws<ArgumentException>(() => store.Move("a", "with,comma"));
            Assert.Equal(before, File.ReadAllBytes(store.FilePath));
        }
        using (Scratch scratch = StoreIn(2))
        {
            var store = new CredentialStore(scratch.Repository);
            Encrypt(scratch, "with,comma", "one");
            store.Move("with,comma", "fine");
            Assert.Equal("one", Decrypt(scratch, "fine"));
            store.MigrateToCsv();
            Assert.Equal("one", Decrypt(scratch, "fine"));
        }
    }

    [Fact]
    public void MvMovesAValueUnderAnEarlierKeyOntoTheCurrentKey()
    {
        using Scratch scratch = StoreIn(3);
        var store = new CredentialStore(scratch.Repository);
        store.Encrypt("a", Bytes("value"));
        string beforeRotation = store.Rows()["a"];
        Seats.Rotate(scratch.Repository);
        Dictionary<string, string> rows = store.Rows();
        rows["a"] = beforeRotation;
        store.Save(rows);

        store.Move("a", "b");

        Assert.Equal(scratch.Repository.RequireStore().RepositoryKeyId, Crypto.ValueKeyId(store.Rows()["b"]));
        Assert.Equal("value", Decrypt(scratch, "b"));
    }
}
