# svnop — manage Subversion credentials with 1Password

[日本語](README.md)

svnop is a Windows tool that keeps the passwords used by TortoiseSVN and the Subversion command line in 1Password.

- **TortoiseSVN**: when an authentication dialog appears, svnop fills it with the value from 1Password and presses OK.
- **TortoiseSVN's svn.exe (console)**: svnop syncs the password cache shared with TortoiseSVN (`%APPDATA%\Subversion`) with 1Password.
- **msys2 svn**: svnop acts as the gpg-agent pinentry and supplies values from 1Password.
- **Recording**: when you enter a password for a new repository, or a password changes, svnop records it in 1Password (after asking).

In 1Password the credentials are stored as "Login" items tagged `svn`. All clients use the same items.

---

## Contents

1. [Requirements](#requirements)
2. [Installation](#installation)
3. [Setup for TortoiseSVN](#setup-for-tortoisesvn)
4. [Setup for msys2](#setup-for-msys2)
5. [Daily use](#daily-use)
6. [Commands](#commands)
7. [Configuration](#configuration)
8. [How it works](#how-it-works)
9. [Security](#security)
10. [Troubleshooting](#troubleshooting)
11. [Uninstalling](#uninstalling)
12. [Building](#building)

---

## Requirements

| Requirement | Notes |
|---|---|
| Windows 10 / 11 | |
| [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) | The installer checks for it |
| 1Password desktop app | Turn on Settings → Developer → "Integrate with 1Password CLI" |
| [1Password CLI](https://developer.1password.com/docs/cli/get-started/) (`op`) | `winget install AgileBits.1Password.CLI` |
| TortoiseSVN | When used with TortoiseSVN |
| msys2 and `pacman -S subversion` | When used with msys2 svn (optional) |

## Installation

Download `svnop-setup-<version>.exe` from [Releases](https://github.com/wtnbgo/svnop/releases) and run it. No administrator rights are needed. It installs per user into `%LOCALAPPDATA%\Programs\svnop`.

You can choose these options during installation.

| Option | What it does |
|---|---|
| Set up for TortoiseSVN | Stores the 1Password vault name, registers the agent to start at logon, and starts the agent (`svnop setup tortoisesvn`) |
| Set up for msys2 svn | Shown only when msys2 (`C:\msys64`) is found (`svnop setup msys2`) |
| Add to PATH | Lets you run `svnop` from Command Prompt or PowerShell |

The output of the setup run by the installer is written to `%APPDATA%\svnop\setup.log`.

Without the installer, extract `svnop-<version>-win-x64.zip` to any folder and run `svnop setup tortoisesvn`.

## Setup for TortoiseSVN

If you chose "Set up for TortoiseSVN" in the installer, step 1 is already done.

1. **Set up**

   ```
   svnop setup tortoisesvn --vault SVN
   ```

   - `--vault`: the 1Password vault for SVN credentials (create it beforehand).
     A dedicated vault is recommended. If omitted, op's default vault is used.
   - Registers the agent (`svnopw.exe`) to start at logon and starts it.

2. **Import existing passwords into 1Password** (if TortoiseSVN already has saved passwords)

   ```
   svnop import --dry-run      # see what can be imported
   svnop import --adopt        # pick numbers to import (or all)
   ```

   With `--adopt`, the imported cache entries come under svnop management (so `svnop lock` can delete them).

3. **Write the cache from 1Password** (for example, when the items were registered on another PC)

   ```
   svnop sync
   ```

From then on, the agent runs `sync` automatically at logon.

## Setup for msys2

msys2 svn (installed with `pacman -S subversion`) cannot use the Windows encrypted cache, so it uses 1Password through the gpg-agent pinentry.

1. Install subversion in msys2.

   ```bash
   pacman -S subversion
   ```

2. Run the setup (from a Windows command prompt or similar).

   ```
   svnop setup msys2
   ```

   It does the following. `svnop setup --remove msys2` undoes it.

   | Target | Change |
   |---|---|
   | `~/.gnupg/gpg-agent.conf` | Points pinentry to `svn-op-pinentry.py`. The original file is kept as `.svnop-bak` |
   | `~/.config/svn-op/config.json` | Creates the pinentry settings |
   | `~/.bashrc` | Adds a few lines, wrapped in `# >>> svnop >>>`, that start gpg-agent in msys2 shells |
   | `~/.subversion/auth/svn.simple` | Creates username-only cache entries for each realm in 1Password, so svn asks 1Password from the first time |

3. Open a new msys2 shell and use `svn`. Passwords come from 1Password through the agent.

> Without the msys2 subversion package, `svn` in an msys2 shell is TortoiseSVN's `svn.exe` from PATH. In that case the TortoiseSVN setup (cache sync) is all you need.

## Daily use

| Situation | What happens |
|---|---|
| A TortoiseSVN authentication dialog appears | The agent fills in the value from 1Password and presses OK |
| A repository not registered in 1Password | The agent shows its own input window. What you enter is recorded in 1Password and in the cache |
| The password was changed on the server | If the same dialog appears again right after auto-fill, the agent treats it as a failure and shows its input window. 1Password is updated with the new value |
| You entered and saved a new password in svn.exe (console) | The agent notices the cache change and, after asking, records it in 1Password |
| msys2 svn | The value travels gpg-agent → pinentry → agent → 1Password. gpg-agent caches it for one hour |
| Register a new repository in advance | `svnop add <URL>` |
| Delete the local cache | `svnop lock` (restore with `svnop sync`), or use the tray menu |

The tray icon (a shield) has a menu with these items:

- Sync from 1Password
- Clear the cache (lock)
- Open log
- Exit

### About 1Password approval

1Password approves CLI access per process. An approval expires after 10 minutes without activity, and after 12 hours at most.
Because the agent does all the 1Password access, you usually see the approval prompt only once after a period of inactivity.

When you run a command such as `svnop list` directly, that command needs its own approval once.

## Commands

See `svnop help <command>` for details.

| Command | Description |
|---|---|
| `setup tortoisesvn \| msys2` | Setup (`--remove` to undo, `--dry-run` to only show the changes) |
| `realm <URL>` | Find the realm (authentication scope) of a URL |
| `add <URL>` | Add to 1Password (also writes the cache) |
| `find <URL\|realm>` | Check whether a realm is registered in 1Password |
| `list` | List registered items |
| `import` | Import the local cache into 1Password |
| `sync` / `lock` / `status` | Sync / delete / show the cache |
| `cache [<URL>]` | List realms in the cache |
| `agent [--stop \| --status]` | Start / stop / check the agent |
| `gpg-init` | Create username-only cache entries for msys2 svn |
| `dialogs` | Inspect TortoiseSVN dialogs on screen (diagnostics) |
| `--version` | Show the version |

The display language follows the Windows display language. Set the environment variable `SVNOP_LANG=ja` or `en` to switch.

## Configuration

`%APPDATA%\svnop\config.json`

```json
{
  "vault": "SVN",
  "op": "",
  "tag": "svn",
  "realmField": "svn_realm",
  "autoSubmit": true,
  "saveAuth": false,
  "syncOnStart": true
}
```

| Key | Default | Meaning |
|---|---|---|
| `vault` | (op default) | The 1Password vault to use |
| `op` | (auto-detected) | Location of `op.exe` |
| `tag` | `svn` | Tag on the items |
| `realmField` | `svn_realm` | Name of the field that holds the realm |
| `autoSubmit` | `true` | Press OK after auto-fill |
| `saveAuth` | `false` | Keep TortoiseSVN's "Save authentication" checked (`false` unchecks it before submitting) |
| `syncOnStart` | `true` | Sync 1Password to the cache when the agent starts |

svnop writes these files in the same folder by itself:

| File | Contents |
|---|---|
| `managed.json` | Record of the cache entries svnop wrote |
| `realm-map.json` | Map from realm to 1Password item ID; contains no secrets |
| `agent.log` / `setup.log` | Logs; passwords are never written |

The msys2 side is configured in `~/.config/svn-op/config.json` (created by `svnop setup msys2`).

## How it works

### Realm

Subversion stores credentials per **realm**. A realm is a string of the form `<scheme://host:port> name`.

| Example | Meaning |
|---|---|
| `<https://svn.example.com:443> VisualSVN Server` | http(s): the Basic / Digest realm |
| `<svn://svn.example.com:3690> 0f1e2d3c-…` | svn://: the svnserve realm (the repository UUID if not configured) |

`svnop realm <URL>` finds this string without sending any credentials.

### 1Password items

| Field | Value |
|---|---|
| Category | Login |
| Tag | `svn` |
| Title | `SVN <user> @ <realm>` |
| Fields | `username` / `password` / `svn_realm` |

There is one item per realm. Items are matched by the `svn_realm` field, so you can rename the title.

### Architecture

```
TortoiseSVN ──(auth dialog)──▶ svnop agent ──▶ op ──▶ 1Password
svn.exe     ──(wincrypt cache in %APPDATA%\Subversion)◀── sync / watch ── svnop agent
msys2 svn   ──▶ gpg-agent ──▶ svn-op-pinentry ──▶ svnop call ──(named pipe)──▶ svnop agent
```

- The agent only fills dialogs shown by TortoiseSVN executables. It checks the install path, so it does not fill similar dialogs from other programs.
- The cache is written with Subversion's standard Windows encryption (DPAPI, per user).
- Only the same user can connect to the named pipe (`\\.\pipe\svnop-<user>`).

## Security

- **Password handling**: svnop never puts passwords in logs or on command lines. Writes to 1Password pass the JSON on standard input.
- **Cache contents**: after `sync`, the cache has the same format and strength as when TortoiseSVN saves passwords itself. If you do not want the cache to stay on a laptop, run `svnop lock` when you finish.
- **Agent access**: while the 1Password approval is valid, the agent answers requests from processes of the same user. This is the same trust model as the gpg-agent cache.
- **Dedicated vault**: a separate vault for SVN limits what op can reach, and can be shared with a team.

## Troubleshooting

| Symptom | What to do |
|---|---|
| The approval prompt keeps appearing | Check that the agent is running (`svnop agent --status`). Commands run directly need one approval each |
| `op.exe not found` | Install the 1Password CLI, or set `op` in `config.json` |
| `account is not signed in` | Turn on "Integrate with 1Password CLI" in the 1Password app and unlock the app |
| TortoiseSVN is not auto-filled | Run `svnop dialogs` to see whether the dialog is detected. Check `%APPDATA%\svnop\agent.log` |
| msys2 svn asks for the password in the terminal | Check that `~/.subversion/auth/svn.simple` has a file for the realm (if not, run `svnop gpg-init --auth-dir …`). Check that gpg-agent is running (`gpgconf --launch gpg-agent`) |
| svn fails when run from an msys2 script | If stdin is not a terminal, svn runs non-interactively and does not call pinentry. Add `--force-interactive` |
| msys2 svn asks to accept a certificate | msys2 svn records accepted certificates separately from TortoiseSVN. Accept it once in a terminal |

## Uninstalling

Uninstall svnop from "Apps & features". This automatically:

- stops the agent
- removes the autostart entry
- undoes the msys2 setup (if it was configured)

1Password items and the local cache are kept. To delete the cache as well, run `svnop lock` before uninstalling.

## Building

```
installer\build.ps1
```

This needs the .NET 10 SDK and Inno Setup 6 or later. It produces these in `dist\`:

| Output | Contents |
|---|---|
| `app\` | The program files |
| `svnop-setup-<version>.exe` | The installer |
| `svnop-<version>-win-x64.zip` | The program files as a zip |

Releases are built by GitHub Actions. Update the version in `svnop/Directory.Build.props` and `CHANGELOG.md`, then push a tag:

```
git tag v0.1.0
git push origin v0.1.0
```

When the build finishes, a Release with the installer and the zip attached is created. The release notes come from the matching section of `CHANGELOG.md`.

| Directory | Contents |
|---|---|
| `svnop/` | The program (C#): `svnop.exe` (commands) and `agentw/` (`svnopw.exe`, the resident agent) |
| `msys2/svn-op-pinentry.py` | The pinentry for msys2 |
| `installer/` | Inno Setup script and build script |
| `.github/workflows/` | GitHub Actions (build and release) |
| `docs/svn-1password-feasibility.md` | Research notes (Japanese) |
| `docs/tortoisesvn-tool-plan.md` | Design and test notes (Japanese) |

## License

[MIT](LICENSE)
