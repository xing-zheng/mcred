---
description: Set up mcred on this seat and in this repository (macOS or Windows) - build and install mcred if it is missing or older than this plugin, guide the person through the seat's root key, then join the repository's store or start one. `check` reports and changes nothing. Never runs `mcred init`, `seat invite`, `seat join`, `rotate` or `migrate`; never prints a root key or a value
argument-hint: "[check]"
disable-model-invocation: true
---

# /mcred:init

This skill is not the command `mcred init`. That command takes a password, so only the person runs it.

The skill can run again at any time: it changes only what is missing or out of date.

- **`<plugin root>`** is `${CLAUDE_PLUGIN_ROOT}`: the installed plugin, with `mcred.csproj`, the code and the tests.
- **This skill's directory** is `${CLAUDE_SKILL_DIR}`. It holds [install.md](install.md), [join.md](join.md) and
  [new-store.md](new-store.md). Read each one when a step below names it. Those files say `<plugin root>` for the path
  above.

## Arguments

`$ARGUMENTS`:

- none: the whole run, `## 1` to `## 5`.
- `check`: `## 1` and `## 5` alone. Nothing is downloaded, built, written or set.

Any other argument: print the argument hint and stop.

## Rules

- **Never run `mcred init`, `mcred seat invite`, `mcred seat join`, `mcred rotate` or `mcred migrate`.** They take a
  password or a one-off root key, print one, or rewrite the store.
- **The person runs `mcred init` and `mcred seat join` in their own terminal**, from inside the repository: on Windows a
  PowerShell console, never mintty and never `!` in this session. There stdin is not a terminal, so the secret would be
  read as redirected input, or stay in the transcript.
- **Never print a root key, a one-off root key or a value.** The check of a root key below reads its presence only. If
  the person pastes a secret into the chat, say that it is exposed and do not use it.
- **Ask with AskUserQuestion** before the .NET SDK is installed, before anything outside the publish directory is
  written, and before each write in the repository.
- **Never commit or push** without the person's word.
- Stop at the first failure, and report it with its output. An error from mcred is one `mcred: …` line on stderr.
- Windows: work in the PowerShell tool, with Windows paths. macOS: the Bash tool.

## 1. Look

Read-only. Collect these, and keep them for the report:

1. **The system and its runtime identifier, `<rid>`:**
   - Windows: `win-x64`, or `win-arm64` when `$env:PROCESSOR_ARCHITECTURE` is `ARM64`.
   - macOS: `osx-arm64`, or `osx-x64` when `uname -m` is `x86_64`.
   - Any other system: say that mcred runs on macOS and Windows only, and stop.
2. **What this plugin installs:** from `<plugin root>/mcred.csproj`, `<Version>` is `<target>`, and `<TargetFramework>`
   gives the SDK's `<channel>` (`net10.0` gives `10.0`). Read them; do not assume them.
3. **The SDK:** `dotnet --list-sdks`, and the user-local one if it exists (`~/.dotnet/dotnet --list-sdks`; Windows
   `$env:USERPROFILE\.dotnet\dotnet.exe`). It is there when a line starts with the channel's major version. `<dotnet>` is
   the full path of the `dotnet` that has it.
4. **The installed copy:** the publish directory `<pub>` is `~/.local/share/mcred` (Windows
   `$env:USERPROFILE\.local\share\mcred`). `<mcred>` is `<pub>/mcred` (Windows `<pub>\mcred.exe`). Its version is
   `<mcred> --version`, or `none`.
5. **Launchers and `PATH`:**
   - macOS: `~/.local/bin/mcred` should be exactly `#!/bin/sh` and `exec "$HOME/.local/share/mcred/mcred" "$@"`,
     executable, with `~/.local/bin` on `PATH`.
   - Windows: `<pub>` on the **user** `PATH`, read raw from the registry:
     `(Get-Item 'HKCU:\Environment').GetValue('Path', '', 'DoNotExpandEnvironmentNames')`.
   - Windows with Cygwin (`C:\cygwin64\bin\bash.exe` exists): its `~/bin/mcred` should be exactly `#!/bin/sh` and
     `exec "/cygdrive/c/Users/<user>/.local/share/mcred/mcred.exe" "$@"`, executable, with LF line ends, and `~/bin` on
     Cygwin's `PATH`. Run Cygwin's bash with `HOME` removed for the call, so it takes its own home:
     `Remove-Item Env:HOME -ErrorAction SilentlyContinue; & 'C:\cygwin64\bin\bash.exe' -lc 'cygpath -w ~; command -v mcred; cat ~/bin/mcred'`.
6. **Variables:**
   - `MCRED_DECRYPTED_SSH_KEYS_PATH`, the clear SSH tree. Windows: the user variable
     (`[Environment]::GetEnvironmentVariable('MCRED_DECRYPTED_SSH_KEYS_PATH', 'User')`) and, with Cygwin, its
     `~/.bash_profile` line. macOS: the login shell's profile (`~/.zprofile` for zsh, `~/.bash_profile` for bash; see
     `$SHELL`) and the current value.
   - The retired `MCRED_ENCRYPTED_CREDENTIALS`, `MCRED_ENCRYPTED_SSH_KEYS`, `MCRED_ENCRYPTED_SSL_KEYS`,
     `MCRED_DECRYPTED_SSL_KEYS` and `MCRED_DECRYPTED_SSH_KEYS`: any that is set.
   - `DOTNET_ROOT`, only when the SDK is user-local.
7. **The root key, by its presence only:**
   - Windows: `cmdkey /list:mcred-root-key/root-v1` shows a `Target:` line, or `* NONE *`.
   - macOS: `security find-generic-password -s mcred-root-key -a root-v1 >/dev/null 2>&1 && echo present || echo absent`.
     Never `-g` or `-w`.
8. **This repository:** `git rev-parse --show-toplevel` is `<repo>`, if the current directory is in a git repository.
   When mcred is installed, run `<mcred> status` from inside `<repo>`.

With `check`: go to `## 5`.

## 2. Install or update

Compare the installed version with `<target>` as numbers:

- **None, or older:** follow [install.md](install.md) from its start.
- **The same:** nothing to build. If `## 1` found a launcher, `PATH` or a variable missing, follow only `## 4 Provision`
  of [install.md](install.md).
- **Newer:** say so, and never downgrade. Then the same as above.

For the rest of the run, call mcred as `<mcred>`, by its full path: this session's `PATH` does not change.

## 3. The root key

If the root key is present, go on to `## 4`. Otherwise run `<mcred> status` in `<repo>`, when there is one, and ask one
question (AskUserQuestion): **Do you already have an mcred password, used on another seat?**

- **Yes.** The person runs `mcred init` with that same password, in a repository whose store has a slot for it: `<repo>`,
  or another of theirs on this seat.
  - The same password gives the same root key, so this seat opens every slot that key already has, with no invite and no
    change to any repository.
  - mcred refuses a different password and saves nothing. The person just runs it again.
  - If no repository on this seat has a slot for their key, nothing can check the password. The person then types the
    same password at `mcred seat join`'s prompt ([join.md](join.md)). Say that this one is not checked, so a typing
    mistake would make a different root key.
- **No, and `<repo>` has a store** (`status` shows `format=2` or `format=3`): nothing to do now. `mcred seat join` asks for the new
  password and saves the root key when the join succeeds. Go on to `## 4`.
- **No, and there is no store here:** the person runs `mcred init --new` with a new password of 16 characters or more,
  best a passphrase of six random words. It is the seat's backup: the same password restores the root key on any seat.

Give the exact command with `<mcred>`'s full path, because a terminal opened before `## 2` does not have the new `PATH`.
The person says when it is done, and the presence check of `## 1`.7 runs again.

## 4. This repository

Not in a git repository: the seat's part is done, so go on to `## 5`.

A linked worktree (`git rev-parse --git-dir` differs from `--git-common-dir`) has its own `.mcred-store/`, and a write
there reaches the main branch with the merge. Say so before any write.

Then act on `<mcred> status`, run from inside `<repo>`:

- **`old_store=`:** the store is still `.mcred/`, the name before mcred 2.1.0, and every command refuses it. Ask, then
  `git -C "<repo>" mv .mcred .mcred-store`. Commit on the person's word. A seat on 2.0.0 stops finding the store with it.
  Then run `status` again.
- **`old_credentials=`:** a JSON file lies beside a format-3 store, from a merged branch or a stopped `migrate`, and every
  command but `status` and `migrate` refuses it. `mcred migrate` takes its rows into the CSV, and lists any that differ. Name it
  in the report and never run it. `seat list` and `seat join` refuse until then, so stop here for this repository.
- **`format=none`:** no store yet. Ask: **Start the store** / **Not now**. To start it, follow
  [new-store.md](new-store.md).
- **`format=1`:** an old store. `mcred migrate <seat>` turns it into format 2 but rewrites every value, so name it in the
  report and never run it.
- **`format=2` or `format=3`, `seat=none`:** this seat's root key has no slot here, or there is no root key yet. Follow
  [join.md](join.md).
- **`format=2` or `format=3`, `seat=<name>`:** ready. If `ssh_keys=` is above 0 and `ssh_clear=` is set, name
  `mcred decrypt-ssh-keys`, which writes the clear keys there.
- **`format=2`,** with either of the two above: it works as it is, with its credentials in JSON. `mcred migrate` turns them into
  the CSV of format 3: it decrypts nothing and needs no root key, but changes the store for everyone who pulls it, each of whom
  then needs mcred 3.0.0. Name it in the report and never run it.

A store without its `.gitignore` or `.gitattributes`: ask, then `<mcred> repo init` (no seat) completes them.

If `<repo>/.claude/skills/mcred/` exists, it is the copy of the usage skill that mcred wrote before this plugin. It
repeats the plugin's skill `mcred:use`. Show it, and ask before removing it.

## 5. Report

A short table, then what is left:

| Item | Before | Now |
|---|---|---|
| mcred in `<pub>` | version or none | version |
| .NET SDK (`<channel>`) | | |
| launchers, `PATH` | | |
| `MCRED_DECRYPTED_SSH_KEYS_PATH` | | |
| retired variables, `DOTNET_ROOT` | | |
| root key | present / absent | present / absent |
| this repository's store | `format=`, `seat=` | `format=`, `seat=` |

- With `check`: the `Now` column is what a run would do, and nothing changed.
- **What is left to people:** `mcred init` or `mcred init --new`, an invite and a join, or `migrate`, whichever applies,
  each with its exact command.
- **New terminals** see the changed `PATH` and variables. This session and the terminals already open keep the old ones.
- In a repository with a store, a session loads the skill `mcred:use` by itself when a task needs a secret.
