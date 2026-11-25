using System.Security.Cryptography;
using System.Text;
using Xunit;

// Seats: two seats in one process - the owner's root key and a new developer's - by swapping the in-memory store.
public class SeatTests
{
    private static Scratch NewStore(out byte[] ownerKey)
    {
        var scratch = new Scratch(withStore: false);
        ownerKey = scratch.RootKey;
        Seats.InitStore(scratch.Repository, "owner");
        return scratch;
    }

    private static void UseRootKey(byte[]? key) => RootKeyStore.Current = new MemoryRootKeyStore(key);

    private static string Decrypt(Scratch scratch, string name) =>
        Encoding.UTF8.GetString(new CredentialStore(scratch.Repository).Decrypt(name));

    [Fact]
    public void ANewStoreIsFormat3AndItsValuesAreV3()
    {
        using Scratch scratch = NewStore(out _);
        var store = new CredentialStore(scratch.Repository);
        store.Encrypt("a", Encoding.UTF8.GetBytes("value"));
        Assert.Equal("value", Decrypt(scratch, "a"));
        Assert.StartsWith("v3.", store.Rows()["a"]);
        RepositoryMetadata metadata = scratch.Repository.RequireStore();
        Assert.Equal(3, metadata.Format);
        Assert.Single(metadata.Slots);
        Assert.Equal("owner", metadata.Slots[0].Name);
        Assert.DoesNotContain("\r", File.ReadAllText(scratch.Repository.MetadataPath));
    }

    [Fact]
    public void AnotherRootKeyHasNoSlotAndIsToldHowToJoin()
    {
        using Scratch scratch = NewStore(out _);
        new CredentialStore(scratch.Repository).Encrypt("a", Encoding.UTF8.GetBytes("value"));
        UseRootKey(RandomNumberGenerator.GetBytes(32));
        var error = Assert.Throws<InvalidOperationException>(() => Decrypt(scratch, "a"));
        Assert.Contains("seat join", error.Message);
    }

    [Fact]
    public void TheOwnersFlowInviteThenJoinWithTheDevelopersOwnRootKey()
    {
        using Scratch scratch = NewStore(out byte[] ownerKey);
        new CredentialStore(scratch.Repository).Encrypt("a", Encoding.UTF8.GetBytes("value"));
        string code = Seats.Invite(scratch.Repository, "alice", 72);
        Assert.Matches("^[A-Z2-7]{5}(-[A-Z2-7]{1,5}){5}$", code);

        UseRootKey(RandomNumberGenerator.GetBytes(32));
        Assert.Throws<InvalidOperationException>(() => Decrypt(scratch, "a"));
        Seats.Join(scratch.Repository, "alice", code.ToLowerInvariant().Replace("-", ""));
        Assert.Equal("value", Decrypt(scratch, "a"));
        RepositoryMetadata metadata = scratch.Repository.RequireStore();
        Assert.Equal(2, metadata.Slots.Count);
        Assert.All(metadata.Slots, slot => Assert.Equal(Slot.SeatKind, slot.Kind));
        Assert.Equal("owner", metadata.Slots.Single(s => s.Name == "alice").AddedBy);

        // The developer writes; the owner reads it.
        new CredentialStore(scratch.Repository).Encrypt("b", Encoding.UTF8.GetBytes("from alice"));
        UseRootKey(ownerKey);
        Assert.Equal("from alice", Decrypt(scratch, "b"));
    }

    [Fact]
    public void AUsedCodeOpensNothingNew()
    {
        using Scratch scratch = NewStore(out _);
        string code = Seats.Invite(scratch.Repository, "alice", 72);
        UseRootKey(RandomNumberGenerator.GetBytes(32));
        Seats.Join(scratch.Repository, "alice", code);
        UseRootKey(RandomNumberGenerator.GetBytes(32));
        Assert.Throws<InvalidOperationException>(() => Seats.Join(scratch.Repository, "alice", code));
    }

    [Fact]
    public void AnExpiredOneOffIsRefused()
    {
        using Scratch scratch = NewStore(out _);
        Func<DateTimeOffset> clock = Seats.Now;
        try
        {
            string code = Seats.Invite(scratch.Repository, "alice", 1);
            Seats.Now = () => DateTimeOffset.UtcNow.AddHours(2);
            UseRootKey(RandomNumberGenerator.GetBytes(32));
            var error = Assert.Throws<InvalidOperationException>(() => Seats.Join(scratch.Repository, "alice", code));
            Assert.Contains("expired", error.Message);
        }
        finally
        {
            Seats.Now = clock;
        }
    }

    [Fact]
    public void AnEditedExpiryOrNameDoesNotOpen()
    {
        using Scratch scratch = NewStore(out _);
        string code = Seats.Invite(scratch.Repository, "alice", 1);
        string json = File.ReadAllText(scratch.Repository.MetadataPath);
        RepositoryMetadata metadata = scratch.Repository.RequireStore();
        string expires = metadata.Slots.Single(s => s.Name == "alice").Expires!;
        File.WriteAllText(scratch.Repository.MetadataPath, json.Replace(expires, "2099-01-01T00:00:00Z"));
        UseRootKey(RandomNumberGenerator.GetBytes(32));
        Assert.Throws<InvalidDataException>(() => Seats.Join(scratch.Repository, "alice", code));
    }

    [Fact]
    public void AWrongCodeIsRefused()
    {
        using Scratch scratch = NewStore(out _);
        Seats.Invite(scratch.Repository, "alice", 72);
        UseRootKey(RandomNumberGenerator.GetBytes(32));
        Assert.Throws<InvalidDataException>(() =>
            Seats.Join(scratch.Repository, "alice", Crypto.ToCode(RandomNumberGenerator.GetBytes(16))));
    }

    [Fact]
    public void RotateKeepsEveryValueDropsTheOtherSeatsAndKeepsAnEarlierValueReadable()
    {
        using Scratch scratch = NewStore(out byte[] ownerKey);
        var store = new CredentialStore(scratch.Repository);
        store.Encrypt("a", Encoding.UTF8.GetBytes("value"));
        string beforeRotation = store.Rows()["a"];
        string code = Seats.Invite(scratch.Repository, "alice", 72);
        byte[] aliceKey = RandomNumberGenerator.GetBytes(32);
        UseRootKey(aliceKey);
        Seats.Join(scratch.Repository, "alice", code);

        UseRootKey(ownerKey);
        (int count, IReadOnlyList<string> dropped) = Seats.Rotate(scratch.Repository);
        Assert.Equal(1, count);
        Assert.Equal(["alice"], dropped);
        Assert.NotEqual(beforeRotation, store.Rows()["a"]);
        Assert.Equal("value", Decrypt(scratch, "a"));

        // A value written on a branch before the rotation, merged after it, opens through the earlier key.
        Dictionary<string, string> rows = store.Rows();
        rows["from-a-branch"] = beforeRotation;
        store.Save(rows);
        Assert.Throws<InvalidDataException>(() => Decrypt(scratch, "from-a-branch"));   // its name is authenticated
        rows["a"] = beforeRotation;
        store.Save(rows);
        Assert.Equal("value", Decrypt(scratch, "a"));

        UseRootKey(aliceKey);
        Assert.Throws<InvalidOperationException>(() => Decrypt(scratch, "a"));
    }

    [Fact]
    public void RemoveRefusesTheLastSeat()
    {
        using Scratch scratch = NewStore(out _);
        Assert.Throws<InvalidOperationException>(() => Seats.Remove(scratch.Repository, "owner"));
        Seats.Invite(scratch.Repository, "alice", 72);
        Seats.Remove(scratch.Repository, "alice");
        Assert.Single(scratch.Repository.RequireStore().Slots);
    }

    [Fact]
    public void MigrateTurnsAFormat1StoreIntoFormat2AndIsRerunnable()
    {
        using var scratch = new Scratch();
        var store = new CredentialStore(scratch.Repository);
        store.Encrypt("a", Encoding.UTF8.GetBytes("value"));
        string key = scratch.Write("src/k.pem", "KEY");
        KeyStore.Add(scratch.Repository, KeyStoreKind.Ssh, key, "cp", replace: false);
        Assert.StartsWith("v2.", store.Rows()["a"]);

        Assert.Equal((2, 0), Seats.Migrate(scratch.Repository, "owner"));
        Assert.Equal(2, scratch.Repository.RequireStore().Format);
        Assert.StartsWith("v3.", store.Rows()["a"]);
        Assert.Equal("value", Decrypt(scratch, "a"));
        Assert.Equal((0, 1), KeyStore.DecryptAll(scratch.Repository, KeyStoreKind.Ssh, replace: false));
        Assert.Equal((0, 2), Seats.Migrate(scratch.Repository, "owner"));
    }

    [Fact]
    public void MigrateWritesNothingWhenOneValueFails()
    {
        using var scratch = new Scratch();
        var store = new CredentialStore(scratch.Repository);
        store.Encrypt("a", Encoding.UTF8.GetBytes("value"));
        Dictionary<string, string> rows = store.Rows();
        rows["broken"] = rows["a"];   // authenticated under the name "a"
        store.Save(rows);
        Assert.Throws<InvalidDataException>(() => Seats.Migrate(scratch.Repository, "owner"));
        Assert.Equal(1, scratch.Repository.RequireStore().Format);
        Assert.StartsWith("v2.", store.Rows()["a"]);
    }

    [Fact]
    public void AStillV2ValueInFormat2NamesMigrate()
    {
        using var scratch = new Scratch();
        var store = new CredentialStore(scratch.Repository);
        store.Encrypt("a", Encoding.UTF8.GetBytes("value"));
        string v2 = store.Rows()["a"];
        Seats.Migrate(scratch.Repository, "owner");
        Dictionary<string, string> rows = store.Rows();
        rows["a"] = v2;
        store.Save(rows);
        var error = Assert.Throws<InvalidOperationException>(() => Decrypt(scratch, "a"));
        Assert.Contains("migrate", error.Message);
        Assert.Equal((1, 0), Seats.Migrate(scratch.Repository, "owner"));
        Assert.Equal("value", Decrypt(scratch, "a"));
    }

    [Fact]
    public void TheCodeRoundTripsAndNamesAreChecked()
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(16);
        Assert.Equal(bytes, Crypto.FromCode(Crypto.ToCode(bytes), 16));
        Assert.Equal(26, Crypto.ToCode(bytes).Replace("-", "").Length);
        using Scratch scratch = NewStore(out _);
        Assert.Throws<ArgumentException>(() => Seats.Invite(scratch.Repository, "Bad Name", 72));
        Assert.Throws<ArgumentException>(() => Seats.Invite(scratch.Repository, "alice", 0));
        Assert.Throws<InvalidOperationException>(() => Seats.Invite(scratch.Repository, "owner", 72));
    }

    // mcred init and seat join: a password's root key is saved only once it has been checked, or the person asked for a new one.
    private const string Password = "a long enough test passphrase";

    private static byte[] RootKeyOf(string password) => RootKeyStore.DecodeRootKey(RootKeyDerivation.Derive(password));

    // A store whose one seat is the password's, with one value, on a seat that has no root key yet.
    private static Scratch StoreOfPassword(string password)
    {
        var scratch = new Scratch(withStore: false);
        UseRootKey(RootKeyOf(password));
        Seats.InitStore(scratch.Repository, "owner");
        new CredentialStore(scratch.Repository).Encrypt("a", Encoding.UTF8.GetBytes("value"));
        UseRootKey(null);
        return scratch;
    }

    private static string NoPasswordIsAsked()
    {
        Assert.Fail("A password was asked for.");
        return "";
    }

    [Fact]
    public void InitWithTheSlotsPasswordSavesTheRootKey()
    {
        using Scratch scratch = StoreOfPassword(Password);
        (string slot, string store) = Seats.InitExisting(scratch.Root, () => Password);
        Assert.Equal("owner", slot);
        Assert.Equal(scratch.Repository.Store, store);
        Assert.Equal(RootKeyOf(Password), RootKeyStore.ReadRootKey());
        Assert.Equal("value", Decrypt(scratch, "a"));
    }

    [Fact]
    public void InitWithATwelveCharacterPasswordThatOpensASlotSaves()
    {
        using Scratch scratch = StoreOfPassword("twelve chars");
        Assert.Equal("owner", Seats.InitExisting(scratch.Root, () => "twelve chars").Slot);
        Assert.True(RootKeyStore.Exists());
    }

    [Fact]
    public void InitWithAnotherPasswordSavesNothingAndNamesTheWaysOn()
    {
        using Scratch scratch = StoreOfPassword(Password);
        var error = Assert.Throws<InvalidOperationException>(() =>
            Seats.InitExisting(scratch.Root, () => "another long test passphrase"));
        Assert.Contains("owner", error.Message);
        Assert.Contains("seat join", error.Message);
        Assert.Contains("--new", error.Message);
        Assert.False(RootKeyStore.Exists());
    }

    [Fact]
    public void InitWithNothingToCheckAgainstNamesNewAndAsksNothing()
    {
        using var noGit = new Scratch(withGit: false);
        using var noStore = new Scratch(withStore: false);
        using var formatOne = new Scratch();
        using var oldStore = new Scratch(withStore: false);
        oldStore.Write(".mcred/repository.json", "{}");
        UseRootKey(null);
        foreach (Scratch scratch in new[] { noGit, noStore, formatOne, oldStore })
        {
            var error = Assert.Throws<InvalidOperationException>(() => Seats.InitExisting(scratch.Root, NoPasswordIsAsked));
            Assert.Contains("--new", error.Message);
            Assert.False(RootKeyStore.Exists());
        }
    }

    [Fact]
    public void InitNewSavesInAnyDirectoryAndWantsSixteenCharacters()
    {
        using var scratch = new Scratch(withGit: false);
        UseRootKey(null);
        Assert.Throws<ArgumentException>(() => Seats.InitNew(() => "fifteen chars.."));
        Assert.False(RootKeyStore.Exists());
        Seats.InitNew(() => Password);
        Assert.Equal(RootKeyOf(Password), RootKeyStore.ReadRootKey());
    }

    [Fact]
    public void InitAndInitNewOnASeatWithARootKeyRefuseWithoutAskingForAPassword()
    {
        using Scratch scratch = NewStore(out byte[] ownerKey);
        Assert.Throws<InvalidOperationException>(() => Seats.InitExisting(scratch.Root, NoPasswordIsAsked));
        Assert.Throws<InvalidOperationException>(() => Seats.InitNew(NoPasswordIsAsked));
        Assert.Equal(ownerKey, RootKeyStore.ReadRootKey());
    }

    [Fact]
    public void JoinWithAWrongOneOffKeyLeavesNoRootKey()
    {
        using Scratch scratch = NewStore(out _);
        Seats.Invite(scratch.Repository, "alice", 72);
        UseRootKey(null);
        Assert.Throws<InvalidDataException>(() =>
            Seats.Join(scratch.Repository, "alice", Crypto.ToCode(RandomNumberGenerator.GetBytes(16)), Password));
        Assert.False(RootKeyStore.Exists());
        Assert.Equal(Slot.OneOffKind, scratch.Repository.RequireStore().Slots.Single(s => s.Name == "alice").Kind);
    }

    [Fact]
    public void JoinWithTheRightOneOffKeySavesTheRootKeyAndTheSeatSlot()
    {
        using Scratch scratch = NewStore(out _);
        new CredentialStore(scratch.Repository).Encrypt("a", Encoding.UTF8.GetBytes("value"));
        string code = Seats.Invite(scratch.Repository, "alice", 72);
        UseRootKey(null);
        Seats.CheckInvite(scratch.Repository, "alice");
        Seats.Join(scratch.Repository, "alice", code, Password);
        Assert.Equal(RootKeyOf(Password), RootKeyStore.ReadRootKey());
        Assert.Equal(Slot.SeatKind, scratch.Repository.RequireStore().Slots.Single(s => s.Name == "alice").Kind);
        Assert.Equal("value", Decrypt(scratch, "a"));
    }

    [Fact]
    public void JoinWithAPasswordThatAlreadyHasASlotSavesNothingAndNamesInit()
    {
        using Scratch scratch = StoreOfPassword(Password);
        UseRootKey(RootKeyOf(Password));
        string code = Seats.Invite(scratch.Repository, "alice", 72);
        UseRootKey(null);
        var error = Assert.Throws<InvalidOperationException>(() => Seats.Join(scratch.Repository, "alice", code, Password));
        Assert.Contains("mcred init", error.Message);
        Assert.False(RootKeyStore.Exists());
    }

    [Fact]
    public void CheckInviteRefusesBeforeAnythingIsAsked()
    {
        using Scratch scratch = NewStore(out _);
        var error = Assert.Throws<InvalidOperationException>(() => Seats.CheckInvite(scratch.Repository, "alice"));
        Assert.Contains("No one-off root key", error.Message);
    }
}
