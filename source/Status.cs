// mcred status: key=value lines for people, scripts and gates; it decrypts nothing. Scripts read only store=.
internal static class Status
{
    public static void Print(Repository repository)
    {
        var lines = new List<(string Key, string Value)>
        {
            ("version", Config.Version),
            ("repository", repository.Root),
            ("store", repository.Store),
        };
        // a store under the name before 2.1.0: named, never counted (every other command refuses it with the move)
        if (repository.HasOldStore) lines.Add(("old_store", repository.OldStore));
        if (!repository.HasOldStore && Directory.Exists(repository.Store) && File.Exists(repository.MetadataPath))
        {
            RepositoryMetadata metadata = repository.ReadStore();
            // a JSON file beside a CSV store: named, and not counted (every other command but migrate refuses it)
            if (repository.HasOldCredentials(metadata)) lines.Add(("old_credentials", repository.CredentialsJsonPath));
            lines.Add(("format", metadata.Format.ToString()));
            lines.Add(("credentials", new CredentialStore(repository).Count().ToString()));
            lines.Add(("ssh_keys", KeyStore.Count(repository, KeyStoreKind.Ssh).ToString()));
            lines.Add(("ssl_keys", KeyStore.Count(repository, KeyStoreKind.Ssl).ToString()));
            if (metadata.Format >= 2) lines.AddRange(Seats.StatusLines(repository, metadata));
        }
        else
        {
            lines.Add(("format", "none"));
        }
        lines.Add(("ssh_clear", Config.SshClearRoot() ?? "unset"));
        foreach ((string key, string value) in lines)
            Console.Out.WriteLine($"{key}={value}");
    }
}
