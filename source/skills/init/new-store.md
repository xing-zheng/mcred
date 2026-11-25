# Start this repository's store

Part of `/mcred:init`, its `## 4`, for `format=none`. `<repo>` and `<mcred>` are as `## 1 Look` of `SKILL.md` found them.

## 1. The store

1. The root key must be present. If it is not, `## 3` of `SKILL.md` comes first.
2. Ask for the seat's slot name (AskUserQuestion): `owner` when this is the repository owner's password (every seat of the
   owner shares one root key and one slot), the OS user's name, or another. It must match `^[a-z0-9][a-z0-9-]{0,62}$`.
3. From inside `<repo>`: `<mcred> repo init <seat>`.
4. `<mcred> status` must show `format=3` and `seat=<seat>`.

## 2. The team settings

Ask: **Add the team settings** / **Not now**. They declare the plugin for this repository, for whoever trusts the folder.
They do not replace the install: each collaborator still runs this once, and `claude plugin install mcred@mcred --scope
project` fails until the marketplace is known:

```
claude plugin install mcred --marketplace xing-zheng/mcred
```

To add them, merge these into `<repo>/.claude/settings.json`. Keep what is there, show the difference first, and write it
with LF line ends:

```json
{
  "extraKnownMarketplaces": {
    "mcred": { "source": { "source": "github", "repo": "xing-zheng/mcred" }, "autoUpdate": true }
  },
  "enabledPlugins": { "mcred@mcred": true }
}
```

## 3. The commit

`git -C "<repo>" status --short -- .mcred-store .claude/settings.json` lists the files: `repository.json`, `.gitignore`,
`.gitattributes` and, if it was changed, the settings.

Ask: **Commit them** (message: `mcred: the store, and the team settings for the plugin`, or what was written) / **Leave
them** (Recommended when the repository has its own commit habits). Never push without the person's word.
