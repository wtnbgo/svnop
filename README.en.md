# svnop — manage Subversion credentials with 1Password

[日本語](README.md)

svnop is a Windows tool that keeps the passwords used by TortoiseSVN and the Subversion command line in 1Password.

- 1Password "Login" items (tagged `svn`) are the source of truth. All clients use the same items.
- A resident program (the agent) fills TortoiseSVN's authentication dialogs with values from 1Password.
- Passwords for new repositories, and changed passwords, are recorded in 1Password after asking you.

---

## Contents

1. [Concept: the credential cache mode](#concept-the-credential-cache-mode)
2. [Requirements](#requirements)
3. [Installation](#installation)
4. [Setup (TortoiseSVN)](#setup-tortoisesvn)
5. [Setup (msys2 svn)](#setup-msys2-svn)
6. [Daily use](#daily-use)
7. [Settings](#settings)
8. [Commands](#commands)
9. [How it works](#how-it-works)
10. [Security](#security)
11. [Troubleshooting](#troubleshooting)
12. [Uninstalling](#uninstalling)
13. [Building and releasing](#building-and-releasing)

---

## Concept: the credential cache mode

TortoiseSVN and its command-line `svn.exe` keep passwords in the **credential cache** (`%APPDATA%\Subversion`) and reuse them.
The first thing to decide with svnop is **whether passwords are saved** in that cache.

| | Save (`keep`, recommended) | Do not save (`none`) |
|---|---|---|
| TortoiseSVN (GUI) | Authenticates from the cache. When an entry is missing, svnop fills the authentication dialog and it is saved | svnop fills the authentication dialog every time |
| TortoiseSVN's `svn.exe` (command line) | Authenticates from the cache | **Asks for the password every time** (console input cannot be auto-filled) |
| msys2 svn | Gets the password from 1Password | Gets the password from 1Password |
| Passwords on disk | Yes (encrypted by Windows DPAPI) | No |

**Choose "Save" if you want to use `svn.exe` without typing passwords.**

With "Save", you also decide **whether to sync the cache from 1Password at logon** (`syncOnStart`).
When enabled, passwords changed in 1Password reach the cache automatically at the next logon.

You choose the mode in the installer. Without the installer, the agent shows a selection window at its first start.
You can change it later from Settings in the tray menu or with `svnop config`.

## Requirements

| Requirement | Notes |
|---|---|
| Windows 10 / 11 | |
| [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) | The installer checks for it |
| 1Password desktop app | Turn on Settings → Developer → "Integrate with 1Password CLI" |
| [1Password CLI](https://developer.1password.com/docs/cli/get-started/) (`op`) | `winget install AgileBits.1Password.CLI` |
| TortoiseSVN | |
| msys2 and `pacman -S subversion` | When using msys2 svn (optional) |

## Installation

Download `svnop-setup-<version>.exe` from [Releases](https://github.com/wtnbgo/svnop/releases) and run it.
No administrator rights are needed. It installs per user into `%LOCALAPPDATA%\Programs\svnop`.

The installer asks for the following.

| Page | Contents |
|---|---|
| Language | Display language of the installer and svnop (日本語 / English) |
| Tasks | "Set up for TortoiseSVN", "Set up for msys2 svn" (only when msys2 is found), "Add svnop to PATH" |
| 1Password vault | The vault for SVN credentials (a dedicated vault is recommended) |
| Credential cache | [Whether to save passwords](#concept-the-credential-cache-mode) |
| Sync at logon | Shown only with "Save" |

"Set up for TortoiseSVN" registers the agent to start at logon and starts it.
During setup, 1Password asks for approval once or twice (to check the vault, and for the agent's first sync). Please approve it.
The result is written to `%APPDATA%\svnop\setup.log`.

Without the installer, extract `svnop-<version>-win-x64.zip` to any folder and run `svnop setup tortoisesvn` (it asks for the mode).

## Setup (TortoiseSVN)

If you used the installer, the only thing left is importing existing passwords.

1. **Import existing passwords into 1Password** (if TortoiseSVN already has saved passwords)

   ```
   svnop import --dry-run      # see what can be imported
   svnop import --adopt        # pick numbers to import (or all)
   ```

   With `--adopt`, the imported cache entries come under svnop management (so `svnop lock` can delete them).

2. **Write the cache from 1Password** (with the "Save" mode, when the items are already in 1Password, e.g. registered on another PC)

   ```
   svnop sync
   ```

To set it up by hand:

```
svnop setup tortoisesvn --vault SVN --cache keep --sync-on-start true
```

## Setup (msys2 svn)

msys2 svn (installed with `pacman -S subversion`) cannot use the Windows encrypted cache, so it uses 1Password through the gpg-agent pinentry. This works regardless of the cache mode.

1. Install subversion in msys2.

   ```bash
   pacman -S subversion
   ```

2. Run the setup (from a Windows command prompt or similar). Not needed if you chose the installer task.

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

3. Open a new msys2 shell and use `svn`.

> Without the msys2 subversion package, `svn` in an msys2 shell is TortoiseSVN's `svn.exe` from PATH.
> It then behaves as TortoiseSVN's `svn.exe`, so use the "Save" cache mode.

## Daily use

| Situation | What happens |
|---|---|
| A TortoiseSVN authentication dialog appears | The agent fills in the value from 1Password and presses OK (with a notification). With "Save", the value is also saved to the cache when authentication succeeds |
| A repository not registered in 1Password | The agent shows its own input window. What you enter is recorded in 1Password, then filled into the dialog |
| The password was changed on the server | If the same dialog appears again right after auto-fill, the agent treats it as a failure and shows its input window. 1Password is updated with the new value |
| You entered and saved a password in `svn.exe` | The agent notices the cache change and, after asking, records it in 1Password |
| You changed a password in 1Password | With "Save" + sync at logon, it is applied at the next logon. To apply it now, use "Sync from 1Password" in the tray or `svnop sync` |
| Register a new repository in advance | `svnop add <URL>` |
| Temporarily delete the local cache | "Clear the cache" in the tray or `svnop lock` (restore with sync) |

### Tray menu

| Item | What it does |
|---|---|
| Sync from 1Password | Brings the cache in line with 1Password (only with "Save") |
| Clear the cache (lock) | Deletes the cache entries written by svnop |
| Settings | See the table below |
| Help (README) | Opens this README |
| Open log | Opens `agent.log` |
| Exit | Stops the agent |

The Settings submenu:

| Item | What it does |
|---|---|
| Vault | The vault in use (display only) |
| Passwords in the credential cache: Save / Do not save | Switches the mode. Switching to "Do not save" asks whether to delete the existing cache; switching to "Save" asks whether to sync now |
| Sync from 1Password at logon | Only with "Save" |
| Notify when auto-filled | |
| Press OK after auto-fill | When off, the values are filled in and you press OK yourself |
| Language | Auto (follow Windows) / 日本語 / English |
| Open config file | Opens `config.json` |

### About 1Password approval

1Password approves CLI access per process. An approval expires after 10 minutes without activity, and after 12 hours at most.
Because the agent does all the 1Password access, you usually see the approval prompt only once after a period of inactivity.

When you run a command such as `svnop list` directly, that command needs its own approval once.

## Settings

Settings live in `%APPDATA%\svnop\config.json`. You can change them in three ways:

- Settings in the tray menu
- `svnop config <key> <value>`
- editing the file directly (the agent re-reads it when its menu is opened)

| Key | Default | Meaning |
|---|---|---|
| `cacheMode` | (not chosen) | `keep`: save in the credential cache / `none`: do not save |
| `syncOnStart` | `true` | With `keep`, sync the cache from 1Password at logon (when the agent starts) |
| `vault` | (op default) | The 1Password vault to use |
| `language` | `auto` | Display language: `auto` (follow Windows) / `ja` / `en` |
| `notifyOnFill` | `true` | Notify when an auth dialog is auto-filled |
| `autoSubmit` | `true` | Press OK after auto-fill |
| `op` | (auto-detected) | Location of `op.exe` |
| `tag` | `svn` | Tag on the items |

The display language is decided in this order:

1. The environment variable `SVNOP_LANG` (`ja` / `en`; to switch a command temporarily)
2. `language` in `config.json`
3. The Windows display language

svnop writes these files in the same folder by itself:

| File | Contents |
|---|---|
| `managed.json` | Record of the cache entries svnop wrote |
| `realm-map.json` | Map from realm to 1Password item ID; contains no secrets |
| `agent.log` / `setup.log` | Logs; passwords are never written |

The msys2 side is configured in `~/.config/svn-op/config.json` (created by `svnop setup msys2`).

## Commands

See `svnop help <command>` for details.

| Command | Description |
|---|---|
| `setup tortoisesvn \| msys2` | Setup (`--remove` to undo, `--dry-run` to only show the changes) |
| `config [key [value]]` | Show / change settings |
| `realm <URL>` | Find the realm (authentication scope) of a URL |
| `add <URL>` | Add to 1Password (also writes the cache with "Save") |
| `find <URL\|realm>` | Check whether a realm is registered in 1Password |
| `list` | List registered items |
| `import` | Import the local cache into 1Password |
| `sync` / `lock` / `status` | Sync / delete / show the cache |
| `cache [<URL>]` | List realms in the cache |
| `agent [--stop \| --status]` | Start / stop / check the agent |
| `gpg-init` | Create username-only cache entries for msys2 svn |
| `dialogs` | Inspect TortoiseSVN dialogs on screen (diagnostics) |
| `--version` | Show the version |

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
svn.exe     ──(credential cache in %APPDATA%\Subversion)◀── sync / watch ── svnop agent   * with "Save"
msys2 svn   ──▶ gpg-agent ──▶ svn-op-pinentry ──▶ svnop call ──(named pipe)──▶ svnop agent
```

- The agent only fills dialogs shown by TortoiseSVN executables. It checks the install path, so it does not fill similar dialogs from other programs.
- "Save authentication" in the dialog is left checked with "Save" and unchecked with "Do not save".
- The cache is written with Subversion's standard Windows encryption (DPAPI, per user).
- Only the same user can connect to the named pipe (`\\.\pipe\svnop-<user>`).

## Security

- **Password handling**: svnop never puts passwords in logs or on command lines. Writes to 1Password pass the JSON on standard input.
- **The cache with "Save"**: same format and strength as when TortoiseSVN saves passwords itself. If you do not want passwords left on a laptop, use "Do not save", or lock when you finish.
- **Agent access**: while the 1Password approval is valid, the agent answers requests from processes of the same user. This is the same trust model as the gpg-agent cache.
- **Dedicated vault**: a separate vault for SVN limits what op can reach, and can be shared with a team.

## Troubleshooting

| Symptom | What to do |
|---|---|
| The approval prompt keeps appearing | Check that the agent is running (`svnop agent --status`). Commands run directly need one approval each |
| `op.exe not found` | Install the 1Password CLI, or `svnop config op <path>` |
| `account is not signed in` | Turn on "Integrate with 1Password CLI" in the 1Password app and unlock the app |
| TortoiseSVN is not auto-filled | Run `svnop dialogs` to see whether the dialog is detected. Check `%APPDATA%\svnop\agent.log` |
| `svn.exe` asks for the password every time | Check that the mode is not "Do not save" (`svnop config cacheMode`). With "Save", run `svnop sync` |
| msys2 svn asks for the password in the terminal | Check that `~/.subversion/auth/svn.simple` has a file for the realm (if not, run `svnop gpg-init --auth-dir …`). Check that gpg-agent is running (`gpgconf --launch gpg-agent`) |
| svn fails when run from an msys2 script | If stdin is not a terminal, svn runs non-interactively and does not call pinentry. Add `--force-interactive` |
| msys2 svn asks to accept a certificate | msys2 svn records accepted certificates separately from TortoiseSVN. Accept it once in a terminal |

## Uninstalling

Uninstall svnop from "Apps & features". This automatically:

- stops the agent
- removes the autostart entry
- undoes the msys2 setup (if it was configured)
- removes svnop from PATH

1Password items and the local cache are kept. To delete the cache as well, run `svnop lock` before uninstalling.

## Building and releasing

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
