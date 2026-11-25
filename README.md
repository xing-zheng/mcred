# mcred

A small .NET 10 CLI for macOS and Windows that keeps credentials and private key files as AES-256-GCM ciphertext in a
repository's own store, `.mcred-store/`. The ciphertext is committed; the key that opens it never is: each seat's 32-byte
**root key**, derived from a password and kept in the macOS login Keychain or the Windows Credential Manager, opens the
repository's own random key through the seat's slot (*Seats*).

## The store

mcred works on the repository of the current directory: the nearest directory at or above it that holds `.git` (a
directory, or a file in a linked worktree or a submodule). The walk never passes that root, so a nested repository
without a store gets an error and never the outer repository's store. The store is `<root>/.mcred-store/`:

| Path | What | In git |
|---|---|---|
| `encrypted-credentials.csv` | the credentials: one `name,value` line per credential (below) | yes |
| `encrypted-ssh-keys/` | the SSH key store | yes |
| `encrypted-ssl-keys/` | the SSL (TLS) key store | yes |
| `decrypted-ssl-keys/` | the SSL keys in clear, restored by `decrypt-ssl-keys` | no (`.gitignore`) |
| `repository.json` | the store's `format` and a random `id` | yes |
| `.gitignore`, `.gitattributes` | `/decrypted-ssl-keys/`; `* -text`, so git keeps every byte | yes |

`mcred repo init` writes the last three files, and completes a `.mcred-store/` that lacks one. Every command that reads or
writes a store refuses a repository without `.mcred-store/`, so a command run in the wrong directory never starts a stray
store. A build refuses a `format` it does not know and says to update. Any other file in `.mcred-store/` (a `README.md`
listing the repository's names) is the repository's own.

In a linked worktree, mcred reads and writes the worktree's own `.mcred-store/`: a value written there reaches the main
branch with the merge.

**The credentials file** is made to be read in an editor, whatever its size:

```
name,value
area/thing,v3.<key id>.<salt>.<nonce>.<ciphertext>.<tag>
```

- UTF-8 with no byte order mark, LF line ends, a final newline. The header is exactly `name,value`. The rows are sorted by
  name (ordinal), so adding a row touches one line.
- **It is never quoted.** A name may not hold `,` or `"`, and a value never does (Base64, `.` and hex), so every row splits at
  its one comma, and `cut`, `awk`, `jq` and `Import-Csv` all read it the same way. A script may read the file directly: nothing
  is decrypted, and it needs no mcred.
- **mcred reads it strictly**, because an editor or a merge may have touched it. It refuses these, naming the line, and writes
  nothing: another header or a byte order mark; a CR, which an editor may leave when it saves; a line without exactly one
  comma, which includes git's conflict markers; a duplicate name; a value that does not start with `v3.`. Rows out of order and
  a missing final newline are accepted, and the next write puts them right.
- A store in format 1 or 2 keeps `encrypted-credentials.json` instead: one JSON object, name → ciphertext, sorted and indented
  with LF, its names free of newlines and NUL only (*Formats*).

**The name before 2.1.0 was `.mcred/`.** mcred 2.1.0 reads `.mcred-store/` alone: a repository whose store is still
`.mcred/` (a `repository.json` there) is refused by every command and by `repo init`, with the move named -
`git mv .mcred .mcred-store`, then a commit. No ciphertext changes with it: no value or slot names the store's
directory. `status` names such a store as `old_store=`. A seat therefore takes 2.1.0 together with its repository's
move: 2.0.0 finds no store in `.mcred-store/`.

**One variable:** `MCRED_DECRYPTED_SSH_KEYS_PATH`, the clear SSH tree (for example `~/pem`). Only `add-ssh-key` and
`decrypt-ssh-keys` need it. `~/` and relative paths are accepted; on Windows it is a Windows path. It may not be, lie
inside, or hold a repository's `.mcred-store/`. The older `MCRED_ENCRYPTED_CREDENTIALS`, `MCRED_ENCRYPTED_SSH_KEYS`,
`MCRED_ENCRYPTED_SSL_KEYS`, `MCRED_DECRYPTED_SSL_KEYS` and `MCRED_DECRYPTED_SSH_KEYS` (mcred before 1.0.0) are not read;
when only the last is set, the error names its successor.

## Install

**In Claude Code**, the plugin does it. Once on each seat (Claude Code 2.1.275 or later):

```
/plugin install mcred --marketplace xing-zheng/mcred
```

On an older Claude Code: `/plugin marketplace add xing-zheng/mcred`, then `/plugin install mcred@mcred`. The repository
is private, so the seat's git needs access to it: SSH, or `gh auth login` and `gh auth setup-git`.

Then run `/mcred:init` in any repository. It is not the command `mcred init`. It builds mcred from the plugin's own sources
if it is missing or older than the plugin (the .NET SDK first, after a confirmation), provisions the launchers, `PATH` and
`MCRED_DECRYPTED_SSH_KEYS_PATH` (after another), guides the person through the root key, then joins the repository's store
or starts one. `/mcred:init check` reports and changes nothing. A session then loads the skill `mcred:use` by itself when a
task needs a secret of a repository with a store. The plugin never runs `mcred init`, `seat invite`, `seat join`, `rotate`
or `migrate`.

To update: `claude plugin marketplace update mcred`, `claude plugin update mcred@mcred`, then `/mcred:init` in a new session.
A seat gets a new plugin only when its version changes, and that version always equals mcred's.

A repository can declare the plugin in its `.claude/settings.json` (`/mcred:init` offers this when it starts a store), but
each collaborator still runs the install above once.

**By hand.** Clone `https://github.com/xing-zheng/mcred` beside the repositories that use it (for example
`~/Documents/dev/mcred`). The repository's root is the plugin's marketplace, and `source/` is the plugin: the code, the tests
and the skills. From the clone:

```sh
dotnet publish source/mcred.csproj -c Release -r osx-arm64 --self-contained false -o ~/.local/share/mcred
```

The launcher is `~/.local/bin/mcred` (`#!/bin/sh` + `exec "$HOME/.local/share/mcred/mcred" "$@"`). A plain
`dotnet build` does not replace the installed copy. `mcred --version` prints the version.

**Windows (a seat's Cygwin).** Publish `-r win-x64 --self-contained false` into `C:\Users\<user>\.local\share\mcred`
(it needs the .NET 10 runtime), with a Cygwin launcher on its `PATH`, e.g. `~/bin/mcred`: `#!/bin/sh` +
`exec /cygdrive/c/Users/<user>/.local/share/mcred/mcred.exe "$@"`. Put the publish directory on the user `PATH` too,
so a PowerShell console runs it. `mcred.exe` is a Windows program: the variable and any path argument are Windows
paths (`export MCRED_DECRYPTED_SSH_KEYS_PATH="$(cygpath -w "${HOME}/pem")"`), and from Cygwin it receives the real
Windows path of the current directory, a Cygwin link resolved. Every file and directory it writes on Windows gets an
owner-only ACL (inheritance off, one full-control rule for the current user), which Cygwin shows as an owner-only mode
and its ssh accepts for a private key.

Tests: `dotnet test source/tests/mcred.Tests/mcred.Tests.csproj`. They use an in-memory root key, never the Keychain.

To work on the plugin, run `claude --plugin-dir source` in the clone: `/mcred:init` then builds and installs the working tree.

## The root key

`mcred init` and `mcred init --new` ask for a password twice without echo (or read it from redirected stdin, one trailing
newline dropped), and derive the root key: PBKDF2-HMAC-SHA256 over the password's UTF-8 in Unicode NFC, the
fixed salt `mcred-root-key/root-v1`, 2,000,000 iterations, 32 bytes (`RootKeyDerivation.cs`). It is stored as Base64
in the login Keychain (service `mcred-root-key`, account `root-v1`) or as the Windows generic credential
`mcred-root-key/root-v1`. **The same password always gives the same root key**, on any Mac or Windows seat, so the
password is the backup. The salt is fixed on purpose, which also means anyone holding the ciphertext can try passwords
at that cost per guess: choose a long, unique one. The key can be recomputed without mcred:

```sh
python3 -c "import hashlib,base64,getpass,unicodedata;print(base64.b64encode(hashlib.pbkdf2_hmac('sha256',unicodedata.normalize('NFC',getpass.getpass()).encode(),b'mcred-root-key/root-v1',2000000)).decode())"
```

Which command to run depends on whether you already have a password:

- **`mcred init`, a password already in use** (at least 12 characters). Run it in a repository whose store has a slot for
  that password. Its root key is saved only if it opens a seat slot of that store, so a new password typed by mistake is
  refused and nothing is saved: a new root key would open none of the person's slots. Outside a repository, or where the
  store has no slots to check against (no store, format 1, a store under the old name), it asks for nothing, saves
  nothing, and names `--new`.
- **`mcred init --new`, a first password** (at least 16 characters, best a passphrase of six random words). It runs in any
  directory, with nothing to check the password against.
- **Joining a store** needs neither: `mcred seat join` makes the root key itself (*Seats*).

Both commands take a password only, never a raw root key. They refuse an existing root key before they ask for anything,
and they never display it. On Windows they run in a PowerShell console: in mintty stdin is a pipe, so they would read the
password as redirected input and echo it.

## Commands

```sh
mcred init                         # this seat's root key, from a password that opens a slot of the store here
mcred init --new                   # this seat's root key, from a first password
mcred repo init SEAT               # start a store (format 3) whose first slot is this seat's, named SEAT
mcred repo init                    # complete an existing store's own files
mcred migrate SEAT                 # a format-1 store into format 2, this seat's slot named SEAT
mcred migrate                      # a format-2 store into format 3, or a JSON file beside a format-3 store into its CSV
mcred seat list
mcred seat invite NAME [--expires-hours N]   # a one-off root key for NAME, printed once (72 h by default, at most 168)
mcred seat join NAME               # join with NAME's one-off root key (and make this seat's root key if it has none)
mcred seat remove NAME
mcred rotate                       # a new repository key
mcred status                       # key=value lines: version, repository, store, format, counts, ssh_clear
mcred --version
mcred encrypt NAME -               # the value from stdin, byte for byte
mcred decrypt NAME                 # the value's bytes on stdout, nothing else
mcred list [PREFIX]                # the credentials' names only, sorted, one per line
mcred rm NAME                      # removes one credential
mcred mv OLD NEW                   # renames one credential: encrypted again under the new name, in one write
mcred add-ssh-key FILE [SUBDIRECTORY] [--replace]
mcred decrypt-ssh-keys [--replace]
mcred add-ssl-key FILE [SUBDIRECTORY]
mcred decrypt-ssl-keys
```

- `status` decrypts nothing. A script reads its `store=` line to check which store a directory reaches. It names a JSON file
  beside a format-3 store as `old_credentials=` (*Formats*).
- `encrypt NAME CLEARTEXT` also works, but exposes the value in shell history and process listings. Feed `-` with
  `printf '%s' "$value" | mcred encrypt NAME -`, not `echo`. Encrypting an existing name replaces its value; the file is
  written atomically, sorted by name, with LF on every OS and owner-only permissions.
- `encrypt -` reads stdin's bytes and requires UTF-8; `decrypt` writes the value's bytes, so there is no byte order mark
  on Windows. From Windows PowerShell 5.1 never pipe a value into mcred: it appends a CRLF and sends ASCII. Write the
  bytes to the process's standard input instead.
- The name is authenticated with the value: a row renamed by hand no longer decrypts. `mv` decrypts it under the old name and
  encrypts it under the new one, in memory and in one write. It refuses a `NEW` that exists, and a value under an earlier
  repository key lands under the current one.
- `list` and `rm` decrypt nothing, so they need no root key. `rm` removes the row; git keeps it in the history, and the secret
  stays valid at its source until it is replaced there.
- The retired `add-key` and `decrypt-keys` exit 2, naming their replacements.

## Key stores

`add-ssh-key` / `add-ssl-key` copy the file into the store's clear tree and write its ciphertext under the store's
encrypted tree; the optional subdirectory is relative to both. `decrypt-ssh-keys` / `decrypt-ssl-keys` decrypt every
file of the store in memory first, then write. Files and directories mcred creates are owner-only; a directory that
already exists keeps its own permissions, so let mcred create a clear tree. Symlinks in an encrypted tree are refused.
mcred never deletes: a file removed from a store stays in the clear tree until deleted there.

- **The SSH clear tree is shared** by every repository on the seat, so mcred never replaces a differing file there
  silently. `decrypt-ssh-keys` writes a missing file and skips an identical one; if any file differs from the store's,
  it lists them all and writes nothing. `--replace` replaces them. `add-ssh-key` follows the same rule for its copy.
  It reports `Decrypted N SSH key file(s): W written, U unchanged.`
- **The SSL clear tree is the store's own** (`.mcred-store/decrypted-ssl-keys/`), so `decrypt-ssl-keys` replaces as it
  writes. mcred writes a clear SSL key only while `.mcred-store/.gitignore` holds `/decrypted-ssl-keys/`.
- Each file's path inside its store is authenticated with it, under the store's prefix (`key-file:` for SSH,
  `ssl-key-file:` for SSL). A file renamed or moved inside a store, or copied into the other store, no longer
  decrypts; moving a whole store changes nothing.

## Seats

A store in format 2 or 3 has one random **repository key**, which encrypts every value. Each seat opens it through its
**slot** in `repository.json`: the repository key sealed under that seat's root key. Only a root key can open, or make,
its own slot, so push access alone cannot add a slot that a seat would open. One root key serves every repository the
seat has joined; seats that share a root key (one password) share one slot.

A new developer joins in two steps:

1. A holder runs `mcred seat invite <name>` in the repository, commits and pushes `.mcred-store/repository.json`, and gives
   the printed **one-off root key** to the developer in private (face to face, a call, an end-to-end encrypted chat;
   never email, the repository or a ticket). Run it by hand: a session's transcript would keep the key.
2. The developer runs `mcred seat join <name>` there (on Windows in a PowerShell console). If the seat has no root key
   yet, mcred asks for the developer's own password (at least 12 characters), then for the one-off root key, and saves the
   root key only when the join succeeds. This password cannot be checked: a person who already has one types it exactly,
   since a different one makes a different root key. The one-off slot is replaced by the developer's own slot; they commit
   and push `.mcred-store/repository.json`. The code then opens nothing new.

`/mcred:init` guides the developer's side of this.

What a slot does not protect:

- **The weakest password guards the store.** Anyone who can read the repository can guess a seat's password offline
  against its slot, at 2,000,000 PBKDF2 iterations a guess. Choose long passphrases.
- **A one-off root key is a bearer key, and git keeps its slot.** The expiry stops a late `join`, not a reader of the
  history: the code opens that day's repository key forever. If one may have leaked, run `rotate`.
- **A removed seat keeps what it read**, and its old slot stays in the history. `rotate` protects only what is written
  after it; a real revocation replaces the secrets themselves.
- **Removing a developer includes removing their push access.** After `seat remove` and `rotate`, a person who kept the old
  repository key and can still push can write a value under that key, and it opens for everyone without an error, because
  a value written before a rotation must still open after its merge. `status` counts such values as `values_earlier_key=`.
- Every holder can invite; push access decides who publishes a slot. A seat is an OS user.

`mcred rotate` makes a new repository key and encrypts every value again. Only the rotating seat's slot can be made
again, so every other slot is dropped and named, to be invited again. The earlier keys stay sealed under the new one,
so a value written on a branch before the rotation still decrypts after its merge (`status`: `values_earlier_key`).

`status` adds, in formats 2 and 3: `repository_key=`, `seat=` (this seat's slot, or `none`), `slots=`, `values_v2=`,
`values_earlier_key=`, `one_off_expired=`. `seat list` prints the slots, this seat's marked.

## Formats

`repository.json` names the store's `format`. A build reads up to its own: 2.0.0 reads and writes format 1 exactly as
1.0.0 does, 3.0.0 reads and writes format 2 exactly as 2.x does, and an older build refuses a newer format with a message to
update. `repo init SEAT` starts the newest format, and `rotate`, `seat invite`, `seat join` and `seat remove` keep the store's.

- **Format 1** (mcred 1.0.0): a value is `v2.<salt>.<nonce>.<ciphertext>.<tag>` (Base64 parts): its AES-256-GCM key is
  PBKDF2-HMAC-SHA256 over the root key with the value's random 16-byte salt and 600,000 iterations; the nonce is 12
  random bytes; the associated data is `v2:<name>`, the credential's name or the store's prefix and the file's path.
- **Format 2** (mcred 2.0.0): `repository.json` adds `repositoryKeyId`, `slots` and `earlierKeys`. The repository key is
  32 random bytes; its id, a seat's and a one-off key's are the first 8 bytes of HMAC-SHA256(key, `mcred repository key
  id` / `mcred seat key id` / `mcred one-off key id`), in hex. A value is
  `v3.<key id>.<salt>.<nonce>.<ciphertext>.<tag>`: AES-256-GCM under HKDF-SHA256(repository key, a random 16-byte salt,
  `mcred value v3`), associated data `v3:<key id>:<name>`; no PBKDF2, since the repository key is random. A slot is
  `s1.…` under HKDF-SHA256(the root key or the one-off root key, salt, `mcred slot v1`), its associated data the
  repository's id, the slot's name, kind, key id and expiry, and the repository key's id: a slot copied under another
  name, expiry or repository does not open. An earlier key is `h1.…` under the current repository key. A one-off root
  key is 16 random bytes, shown as 26 base32 characters in groups of five. Everything is .NET's own library.
- **Format 3** (mcred 3.0.0): format 2 with its credentials in `encrypted-credentials.csv` (*The store*). Nothing else differs:
  no value, slot or earlier key names the store's format, so every ciphertext stays as it is.
- `mcred migrate SEAT` decrypts every `v2` value with this seat's root key before it writes anything (nothing is
  written if one fails), writes the format-2 metadata first, then encrypts every value again and checks it back; a run
  that stops is finished by running it again. In format 2 only `migrate` reads `v2`.
- `mcred migrate`, with no seat, takes format 2 to format 3. It decrypts nothing, so it needs no root key. It refuses a `v2`
  value (run `migrate SEAT` first) and a name that holds `,` or `"`, naming each. It writes the CSV atomically, reads it back and
  compares every name and ciphertext, saves `repository.json` as format 3, and deletes the JSON. A run that stops is finished by
  running it again. mcred has no way back from format 3.
- **A JSON file beside a format-3 store** comes from a merged branch or a stopped run, and its rows would otherwise vanish
  without a word. Every command but `status` and `migrate` refuses it, and `status` names it as `old_credentials=`. `mcred
  migrate` finishes the job: a row only in the JSON is added, a row only in the CSV stays, an identical row is dropped. If any row
  differs it lists them and writes nothing: keep one ciphertext for each, in both files, and run it again. Then it deletes the
  JSON. A CSV beside a format-2 store, from a stopped run, gets the same check.
- A branch that changed the JSON meets a modify/delete conflict when it merges a store already in format 3. To resolve it: keep
  the JSON, then run `mcred migrate`.
