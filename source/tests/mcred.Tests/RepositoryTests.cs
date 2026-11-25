using Xunit;

public class RepositoryTests
{
    [Fact]
    public void NoGitAboveIsRefused()
    {
        using var scratch = new Scratch(withGit: false);
        // The temporary directory may itself lie in a repository on some machine; this one never does on the seats.
        var error = Assert.Throws<InvalidOperationException>(() => Repository.Find("/"));
        Assert.Contains("No repository here", error.Message);
    }

    [Fact]
    public void TheNearestGitDirectoryIsTheRoot()
    {
        using var scratch = new Scratch();
        string deep = Path.Combine(scratch.Root, "a", "b");
        Directory.CreateDirectory(deep);
        Assert.Equal(scratch.Root, Repository.Find(deep).Root);
    }

    [Fact]
    public void AWorktreesGitFileIsARoot()
    {
        using var scratch = new Scratch();
        string worktree = Path.Combine(scratch.Root, "worktrees", "w");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: /elsewhere\n");
        Assert.Equal(worktree, Repository.Find(Path.Combine(worktree)).Root);
    }

    [Fact]
    public void ANestedRepositoryNeverFallsBackToTheOuterStore()
    {
        using var scratch = new Scratch();
        string inner = Path.Combine(scratch.Root, "fork");
        Directory.CreateDirectory(Path.Combine(inner, ".git"));
        Repository repository = Repository.Find(inner);
        Assert.Equal(inner, repository.Root);
        var error = Assert.Throws<InvalidOperationException>(() => repository.RequireStore());
        Assert.Contains("repo init", error.Message);
    }

    [Fact]
    public void InitWritesTheThreeFilesOnceAndKeepsThem()
    {
        using var scratch = new Scratch(withStore: false);
        Repository repository = scratch.Repository;
        Assert.Equal(3, repository.Init().Count);
        Assert.Empty(repository.Init());
        Assert.Contains("/decrypted-ssl-keys/", File.ReadAllText(repository.GitIgnorePath));
        Assert.Contains("* -text", File.ReadAllText(repository.GitAttributesPath));
        Assert.Equal(1, repository.RequireStore().Format);
        Assert.DoesNotContain("\r", File.ReadAllText(repository.MetadataPath));
    }

    [Fact]
    public void ANewerFormatIsRefusedWithTheFix()
    {
        using var scratch = new Scratch();
        File.WriteAllText(scratch.Repository.MetadataPath, "{\"format\": 9, \"id\": \"x\"}");
        var error = Assert.Throws<InvalidOperationException>(() => scratch.Repository.RequireStore());
        Assert.Contains("Update mcred", error.Message);
    }

    [Fact]
    public void AStoreWithoutMetadataAsksForRepoInit()
    {
        using var scratch = new Scratch();
        File.Delete(scratch.Repository.MetadataPath);
        var error = Assert.Throws<InvalidOperationException>(() => scratch.Repository.RequireStore());
        Assert.Contains("repo init", error.Message);
    }

    [Fact]
    public void TheStoreIsMcredStore()
    {
        using var scratch = new Scratch();
        Assert.Equal(Path.Combine(scratch.Root, ".mcred-store"), scratch.Repository.Store);
        Assert.True(File.Exists(Path.Combine(scratch.Root, ".mcred-store", "repository.json")));
        Assert.False(Directory.Exists(Path.Combine(scratch.Root, ".mcred")));
    }

    [Fact]
    public void AStoreUnderTheOldNameIsRefusedWithTheMove()
    {
        using var scratch = new Scratch(withStore: false);
        scratch.Write(Path.Combine(".mcred", "repository.json"), "{\"format\": 2, \"id\": \"x\"}");
        Repository repository = scratch.Repository;
        var read = Assert.Throws<InvalidOperationException>(() => repository.RequireStore());
        Assert.Contains("git mv .mcred .mcred-store", read.Message);
        var init = Assert.Throws<InvalidOperationException>(() => repository.Init());
        Assert.Contains("git mv .mcred .mcred-store", init.Message);
        Assert.False(Directory.Exists(repository.Store));
    }

    [Fact]
    public void AnOldStoreBesideTheNewOneIsRefusedToo()
    {
        using var scratch = new Scratch();
        scratch.Write(Path.Combine(".mcred", "repository.json"), "{\"format\": 1, \"id\": \"x\"}");
        var error = Assert.Throws<InvalidOperationException>(() => scratch.Repository.RequireStore());
        Assert.Contains("git mv .mcred .mcred-store", error.Message);
    }

    [Fact]
    public void StatusNamesAnOldStoreAndCountsNothing()
    {
        using var scratch = new Scratch(withStore: false);
        scratch.Write(Path.Combine(".mcred", "repository.json"), "{\"format\": 2, \"id\": \"x\"}");
        TextWriter previous = Console.Out;
        var output = new StringWriter();
        Console.SetOut(output);
        try { Status.Print(scratch.Repository); }
        finally { Console.SetOut(previous); }
        string text = output.ToString();
        Assert.Contains($"store={Path.Combine(scratch.Root, ".mcred-store")}", text);
        Assert.Contains($"old_store={Path.Combine(scratch.Root, ".mcred")}", text);
        Assert.Contains("format=none", text);
        Assert.DoesNotContain("credentials=", text);
    }
}
