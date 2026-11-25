# mcred

The seat-side credential CLI; `README.md` is its reference. Its store is each repository's `.mcred-store/`.

- **Layout:** the repository's root is the plugin's marketplace (`.claude-plugin/marketplace.json`), with this file, the
  README and `plans/`. `source/` is the plugin, and the only directory an install copies: `.claude-plugin/plugin.json`,
  `skills/`, `mcred.csproj`, the code and `tests/`.
- **Build:** `dotnet build source/mcred.csproj -c Release -r osx-arm64` and `-r win-x64`, with no warning
  (`TreatWarningsAsErrors`). **Test:** `dotnet test source/tests/mcred.Tests/mcred.Tests.csproj`. **Install:**
  `/mcred:init`, or the README's `dotnet publish`; a build does not replace the installed copy. **Check the manifests:**
  `claude plugin validate . --strict` and `claude plugin validate source --strict`.
- **Versions:** `plugin.json`'s `version` equals `mcred.csproj`'s `<Version>` (`PluginTests`), and each release is tagged
  `v<Version>`. A seat updates its plugin only when that string changes, so `/mcred:init` never builds a commit between
  releases, and a fix to a skill alone is a patch release.
- **Never** print a decrypted value, the root key or a key file; check a value by its exit status or a comparison in
  memory. **Never** add a way to pass a raw root key: `init` takes a password only.
- `mcred init` saves a root key only if it opens a seat slot of the store here; `mcred init --new` is a first password, of
  16 characters or more. `seat join` saves a new root key only when the join succeeds. A session never runs any of them.
- **Never** run `seat invite` for a real developer from a session: the one-off root key it prints would stay in the
  transcript. A person runs it. `rotate` and `migrate` rewrite the whole store; on a real store, only on the owner's word.
- Real-store checks run with the seat's root key on scratch copies or scratch clear trees, and delete those trees
  afterwards.
- The tests use `MemoryRootKeyStore` (`RootKeyStore.Current`); no test may reach the Keychain or the Credential
  Manager. They run one at a time (one environment variable, one static store); `SeatTests` plays two seats by
  swapping the in-memory root key, and sets `Seats.Now` for the expiry.
- Format changes: a new format number, the old one still read, and tests for both.
- The credentials' file is `CredentialStore`'s alone, and the store's format picks it: `encrypted-credentials.csv` in
  format 3, `encrypted-credentials.json` before it. The CSV is never quoted (a name may not hold `,` or `"`) and is read
  strictly, because scripts read it directly and split at the comma. A JSON file beside a format-3 store is refused by
  everything but `status` and `mcred migrate` (`Repository.RequireStore`, `ReadStore`).
- The store's directory is `Repository.StoreName` (`.mcred-store`); a store under the old name
  (`Repository.OldStoreName`, `.mcred`) is refused with the move, never read.
- Skills (`source/skills/`): `init` is `/mcred:init`, with `install.md`, `join.md` and `new-store.md` beside its `SKILL.md`;
  `use` is `mcred:use`. Keep them in step with the README when a command, an output or a rule changes, and keep them free of
  anything one seat owns (paths, seat names). Work on them with `claude --plugin-dir source` in the clone. Claude Code
  substitutes `${CLAUDE_PLUGIN_ROOT}` only in a `SKILL.md`, so a supporting file says `<plugin root>`.
- Plans for this repository are in `plans/`, and a finished one moves to `plans/archive/`.
- Output contracts callers depend on: `decrypt` writes exactly the value's bytes; `status` prints `key=value` lines
  (`old_store=` beside `store=` while an old store is there, `old_credentials=` while a JSON file lies beside a format-3
  store) and scripts read `store=`; `list` prints only the names, one per line; every line on stdout and stderr ends in
  LF, on Windows too (`Program.cs` sets both writers' `NewLine` first, because Cygwin's bash keeps the CR of a CRLF in
  `store=`); exit 0 on success, 1 on an error (one `mcred: …` line on stderr), 2 on usage.
