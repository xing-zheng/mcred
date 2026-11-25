# Install or update mcred

Part of `/mcred:init`, its `## 2`. `<plugin root>`, `<target>`, `<channel>`, `<rid>`, `<dotnet>`, `<pub>` and `<mcred>` are
as `## 1 Look` of `SKILL.md` found them. mcred is built from the plugin's own sources, so nothing else is cloned.

Each step stops on its first failure: report the output, and do not install a build that failed or whose tests failed.

## 1. The .NET SDK

If the SDK of the channel is there, go on to `## 2`. Otherwise ask (AskUserQuestion), naming the channel:

- **User-local, Microsoft's dotnet-install script (Recommended):** into `~/.dotnet` (Windows `%USERPROFILE%\.dotnet`), with
  no administrator rights. `## 4` then sets `DOTNET_ROOT` and puts it on `PATH`, because a framework-dependent `mcred`
  finds the runtime through `DOTNET_ROOT` outside the system location.
- **System-wide:** Windows: `winget install --id Microsoft.DotNet.SDK.<major> --exact` (a UAC prompt; into
  `C:\Program Files\dotnet`, found without `DOTNET_ROOT`). macOS: `brew install --cask dotnet-sdk` (asks for the password;
  the official package).
- **I'll install it myself:** stop here, with the download page `https://dotnet.microsoft.com/download/dotnet/<channel>`.
  Run `/mcred:init` again afterwards.

The script is downloaded into a new, empty temporary directory and run from there by its path. Nothing else from it is run.

```powershell
# Windows
$dir = New-Item -ItemType Directory -Force (Join-Path $env:TEMP ("dotnet-install-" + [guid]::NewGuid().ToString('N')))
Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile "$dir\dotnet-install.ps1" -UseBasicParsing
powershell -NoProfile -ExecutionPolicy Bypass -File "$dir\dotnet-install.ps1" -Channel <channel> -InstallDir "$env:USERPROFILE\.dotnet"
```

```sh
# macOS
dir="$(mktemp -d)"
curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$dir/dotnet-install.sh"
bash "$dir/dotnet-install.sh" --channel <channel> --install-dir "$HOME/.dotnet"
```

Then check that `<dotnet> --list-sdks` shows the channel, and delete that directory. For the rest of the run, call that
`dotnet` by its full path: this session's `PATH` does not change.

## 2. Build and test, in a temporary copy

The plugin's directory is never written. Copy it into a new temporary directory `<build>`, without any `bin` or `obj`. A tool
call keeps no shell variable, so print the path and use it, as `<build>`, from here on:

```powershell
# Windows: robocopy exits with a code below 8 on success
$build = Join-Path $env:TEMP ("mcred-build-" + [guid]::NewGuid().ToString('N'))
robocopy "<plugin root>" $build /E /XD bin obj /NFL /NDL /NJH /NJS /NP
if ($LASTEXITCODE -ge 8) { throw "robocopy failed: $LASTEXITCODE" }
$build
```

```sh
# macOS
build="$(mktemp -d)"
rsync -a --exclude bin --exclude obj "<plugin root>/" "$build/"
echo "$build"
```

From `<build>`:

```
<dotnet> build mcred.csproj -c Release -r <rid>
<dotnet> test tests/mcred.Tests/mcred.Tests.csproj
```

The build must end with `0 Warning(s)` and `0 Error(s)` (`TreatWarningsAsErrors`). The tests must all pass: they use an
in-memory root key and never reach the Keychain or the Credential Manager.

## 3. Publish

From `<build>`:

```
<dotnet> publish mcred.csproj -c Release -r <rid> --self-contained false -o <pub>
```

On Windows a running `mcred.exe` locks the file, and the publish fails naming it. Report it (close what runs mcred) and
stop.

Then `<mcred> --version` must print `<target>`. Delete `<build>`: `Remove-Item -Recurse -Force "<build>"` on Windows,
`rm -rf "<build>"` on macOS.

## 4. Provision

Build the list of changes from `## 1` of `SKILL.md`, with only what is missing or wrong. An empty list asks nothing: go on.

- **Launchers and `PATH`:**
  - macOS: write `~/.local/bin/mcred` (the two lines of `## 1`.5, `chmod 755`). When `~/.local/bin` is not on `PATH`, append
    `export PATH="$HOME/.local/bin:$PATH"` to the login shell's profile.
  - Windows: append `<pub>` to the user `PATH`, keeping the value's kind and its unexpanded `%…%` entries:

    ```powershell
    $key = 'HKCU:\Environment'
    $raw = (Get-Item $key).GetValue('Path', '', 'DoNotExpandEnvironmentNames')
    $new = (@($raw -split ';' | Where-Object { $_ }) + "$env:USERPROFILE\.local\share\mcred") -join ';'
    Set-ItemProperty -Path $key -Name Path -Value $new -Type ExpandString
    # a registry write tells no running program; setting a user variable through .NET broadcasts the change
    [Environment]::SetEnvironmentVariable('MCRED_DECRYPTED_SSH_KEYS_PATH', [Environment]::GetEnvironmentVariable('MCRED_DECRYPTED_SSH_KEYS_PATH', 'User'), 'User')
    ```

    When the variable is unset as well, set it (below) after the `PATH` write. That broadcasts both.

  - Windows with Cygwin: write its `~/bin/mcred` with LF line ends, from Cygwin's bash with `HOME` removed:
    `mkdir -p ~/bin && printf '#!/bin/sh\nexec "/cygdrive/c/Users/%s/.local/share/mcred/mcred.exe" "$@"\n' "<user>" > ~/bin/mcred && chmod 755 ~/bin/mcred`.
    When `~/bin` is not on its `PATH`, append `export PATH="$HOME/bin:$PATH"` to its `~/.bash_profile`.
- **`MCRED_DECRYPTED_SSH_KEYS_PATH`,** when it is unset or not the same tree everywhere. Ask the person for the clear SSH
  tree (AskUserQuestion, the default first: macOS `~/pem`; Windows with Cygwin `<Cygwin home>\pem` as a Windows path, else
  `%USERPROFILE%\pem`). It may not be, lie inside, or hold a repository's `.mcred-store/`. Then:
  - macOS: append `export MCRED_DECRYPTED_SSH_KEYS_PATH="$HOME/pem"` (the chosen path, `$HOME`-relative where it is) to the
    login shell's profile.
  - Windows: `[Environment]::SetEnvironmentVariable('MCRED_DECRYPTED_SSH_KEYS_PATH', '<Windows path>', 'User')`. With Cygwin,
    also its `~/.bash_profile` line `export MCRED_DECRYPTED_SSH_KEYS_PATH="$(cygpath -w "${HOME}/pem")"` (the chosen tree),
    because Cygwin's value replaces the Windows one there and the two must name the same tree.
  - Do not create the tree. mcred creates it owner-only on the first `decrypt-ssh-keys`.
- **The retired variables** that are set: unset them (Windows:
  `[Environment]::SetEnvironmentVariable(<name>, $null, 'User')`; a profile line: show it and remove it).
- **A user-local SDK:** `DOTNET_ROOT` = `~/.dotnet`, and `~/.dotnet` on `PATH` (macOS: two `export` lines in the profile;
  Windows: the user variable `DOTNET_ROOT` = `%USERPROFILE%\.dotnet`, and that directory appended to the user `PATH` as
  above).

Show the list, each file or variable with its old and new value (for a profile, the lines appended), and ask once
(AskUserQuestion): **Apply all (Recommended)** / **Let me choose** (then one multiSelect question of the changes) / **None**.

Apply what was chosen. A profile or launcher that exists and differs from what this page would write is shown and never
overwritten without the person's word. Read a profile before appending to it, so a line is never added twice.
