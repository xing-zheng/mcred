---
description: How to use mcred in a repository that holds .mcred-store/ - its credentials, SSH keys and SSL keys live there as ciphertext and are read, added and checked with the mcred CLI without a value ever reaching the transcript. Use whenever a task needs a password, token, API key, connection string, private key or certificate of such a repository, or touches .mcred-store/
---

# mcred in this repository

A repository that uses mcred keeps its secrets in **`.mcred-store/`** at its root, as AES-256-GCM ciphertext that is
committed. The key that opens it never is: each seat's root key sits in the macOS login Keychain or the Windows Credential
Manager, and opens the repository's key through the seat's slot in `.mcred-store/repository.json`. The reference is the
README of mcred's repository (`https://github.com/xing-zheng/mcred`).

A repository without `.mcred-store/` has no store, and this skill does not apply. `/mcred:init` can start one.

## Rules

1. **Never print a decrypted value, a clear key file, a root key or a one-off root key.** No bare `mcred decrypt NAME`, no
   `echo`/`cat`/`printf` of one to stdout, no `set -x`, no `env`/`printenv` after exporting one, no `curl -v` (it prints the
   headers), and never into a log, a commit, a tracked file or a message. A value goes from `mcred decrypt` straight into the
   program that uses it (`## Use a value`).
2. **Check a value by its exit status or a comparison in memory**, never by looking at it.
3. **The person runs these, not the session:** `mcred init` and `mcred init --new` (a password), `mcred seat invite` (it
   prints a one-off root key, which would stay in the transcript), `mcred seat join` (it takes that key), and typing any
   secret value. On Windows they run in a PowerShell console or a real terminal, not through `!` in this session.
4. **Only on the owner's explicit word in this conversation:** `mcred rotate` and `mcred migrate` (they rewrite the whole
   store), `mcred seat remove`, and `--replace`.
5. Never edit the ciphertext or `repository.json` by hand, and never commit `.mcred-store/decrypted-ssl-keys/`.
6. A secret the user pastes into the chat is in the transcript already: encrypt it if asked, say that it was exposed, and
   suggest replacing it at its source.

## Orient

mcred works on the repository of the current directory (the nearest directory holding `.git`), so run it from inside this
repository. In a linked worktree it uses the worktree's own `.mcred-store/`; a value written there reaches the main branch
with the merge.

- `mcred status` decrypts nothing and prints `key=value` lines: `store=`, `format=`, `credentials=`, `ssh_keys=`, `ssl_keys=`,
  `seat=` (this seat's slot; `none`: it cannot open the store), `ssh_clear=`.
- **The names** are not secret: `mcred list [PREFIX]` prints them sorted, one per line, and decrypts nothing. The file is
  `.mcred-store/encrypted-credentials.csv`, one `name,value` line per credential, never quoted, so a script may read it directly
  and split at the comma. A `format=2` store keeps `.mcred-store/encrypted-credentials.json` instead, one JSON object, and works
  the same way through mcred; `mcred migrate` moves it to the CSV (rule 4).
  A `.mcred-store/README.md`, when there is one, is the repository's own account of what each name is; keep it current.
- An error exits 1 with one `mcred: …` line on stderr (2 for a usage error). The usual ones, all of which `/mcred:init`
  walks through:
  - no store (`mcred repo init <seat>` starts one; ask first);
  - a store still in `.mcred/` (`git mv .mcred .mcred-store`, then a commit);
  - a JSON file beside a format-3 store, which `status` names as `old_credentials=` and every other command refuses, from a
    merged branch or a stopped `migrate` (`mcred migrate` takes its rows into the CSV, and lists any that differ; rule 4);
  - a line of the CSV, named with its number: a CR from an editor, or a conflict marker from a merge (keep both sides' rows and
    drop the markers: rows out of order are fine, and the next write sorts them);
  - no root key (the person runs `mcred init` with a password already in use, or `mcred init --new` with a first one);
  - `seat=none` (a holder invites this seat, `## Seats`);
  - a format the build does not know, or a build older than this plugin (`/mcred:init` updates mcred).

**The shell.** macOS: bash or zsh. Windows: the Bash tool (Git Bash) or Cygwin's bash, where `mcred` is `mcred.exe` on `PATH`;
a path given to it is a Windows path (`cygpath -w` in Cygwin). **Never move a value through Windows PowerShell 5.1**: it
appends a CRLF to what it pipes, sends ASCII and re-decodes what it reads. `mcred status` and the commands that print no value
are fine there.

## Use a value

```sh
mcred decrypt registry/token | docker login -u ci --password-stdin registry.example.com         # on stdin
PGPASSWORD="$(mcred decrypt sql/app)" psql -h db.example.com -U app app                         # one command's env
printf 'Authorization: Bearer %s\n' "$(mcred decrypt api/token)" | curl -sS -H @- https://api.example.com/v1/x
```

- `decrypt` writes exactly the value's bytes; `$(…)` drops trailing newlines. The transcript shows the command, never the
  expansion.
- A tool that wants a file: write it outside the repository (or to an ignored path), owner-only, and delete it after:
  `umask 077; mcred decrypt NAME > "$tmp/x"`. Never read it back.
- **Present?** `mcred decrypt NAME >/dev/null && echo present`. **The same?**
  `cmp -s <(mcred decrypt NAME) <(other-command) && echo same || echo differs`. **Its length:** `mcred decrypt NAME | wc -c`.

## Add or replace a value

- **From a program**, piped, byte for byte: `openssl rand -base64 32 | tr -d '\n' | mcred encrypt NAME -`.
- **From a variable**: `printf '%s' "$value" | mcred encrypt NAME -`. Never `echo`, and never `mcred encrypt NAME CLEARTEXT`
  (shell history, process listings, the transcript).
- **From the person**: ask them to run, in their own terminal (on Windows a Git Bash or Cygwin terminal),
  `IFS= read -rs v && printf '%s' "$v" | mcred encrypt NAME - ; unset v`, and to say when it is done.
- Encrypting an existing name replaces its value. Follow the names already there (`area/thing`). A name may not hold `,` or
  `"` in a format-3 store.
- **To rename:** `mcred mv OLD NEW`. The name is authenticated with the value, so never edit a name in the file: `mv` encrypts
  the value again under the new name, and refuses a `NEW` that exists.
- **To remove:** `mcred rm NAME`, when the user asks. The history keeps the row, and the secret stays valid at its source until
  it is replaced there.
- Then check it (`mcred decrypt NAME >/dev/null && echo ok`) and, when the user asks for a commit, commit
  `.mcred-store/encrypted-credentials.csv` (`format=2`: `.json`) with the change that uses the value.

## SSH and SSL keys

- `MCRED_DECRYPTED_SSH_KEYS_PATH` names the seat's clear SSH tree (`~/pem`, say), shared by every repository on the seat;
  `status` shows it as `ssh_clear=`. `mcred decrypt-ssh-keys` writes the missing files and skips the identical ones; if any
  differs it lists them and writes nothing (`--replace` only on the user's word). Use a key by its path,
  `ssh -i "$MCRED_DECRYPTED_SSH_KEYS_PATH/<file>"`; never print it.
- `mcred add-ssh-key FILE [SUBDIRECTORY]` copies the file into the clear tree and its ciphertext into
  `.mcred-store/encrypted-ssh-keys/` (commit that). `mcred add-ssl-key FILE [SUBDIRECTORY]` and `mcred decrypt-ssl-keys` do the
  same for `.mcred-store/encrypted-ssl-keys/`, with the clear copies in `.mcred-store/decrypted-ssl-keys/` (ignored by git,
  replaced as written).
- A file's path inside its store is authenticated: do not move or rename files in an encrypted tree; add the key again under
  its new path. mcred never deletes: a file dropped from a store stays in the clear tree.

## Seats

`mcred seat list` prints the slots, this seat's marked. A new developer joins in two steps, both run by people: a holder runs
`mcred seat invite <name>` by hand, commits and pushes `.mcred-store/repository.json` and hands over the printed one-off root
key in private; the developer runs `mcred seat join <name>` here and commits `.mcred-store/repository.json`. `/mcred:init`
guides the developer's side. The session may explain the steps and make those commits when asked; it never runs the two
commands.

**Removing a developer includes removing their push access.** After `mcred seat remove` and `mcred rotate`, a person who kept
the old repository key and can still push can write a value under that key, and it opens for everyone without an error.
`status` counts such values as `values_earlier_key=`.
