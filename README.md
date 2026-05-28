# BG3 Mod Helper — Mod Update Helper Edition

**Mod update checker and installer for Baldur's Gate 3**

[![License: GPL v3](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)

---

## What is this?

BG3 Mod Helper is a standalone Windows utility that helps you track and install updates for your Baldur's Gate 3 mods. It checks both **Nexus Mods** and **mod.io** simultaneously so you don't have to visit each mod page manually.

---

## Features

- **Update checking** — Scans your installed `.pak` mods and checks for updates on Nexus Mods and mod.io
- **Auto-download** — Automatically downloads and installs updates (mod.io for all users; Nexus for Premium members)
- **NXM link handler** — Supports the "Download with Manager" button on Nexus Mods
- **Folder watcher** — Detects newly downloaded mod archives and guides you through installation
- **Drag-and-drop install** — Drop a mod archive onto the app to install
- **Mod identification** — Identifies unknown mods via Nexus MD5 search
- **Version preservation** — Lock specific mods at their current version to exclude them from update checks
- **Download history** — Tracks all downloads with version and timestamp

---

## Requirements

- Windows 10 / 11 (x64)
- [.NET 8 Desktop Runtime x64](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- Nexus Mods API key (free) — [Get yours here](https://www.nexusmods.com/settings/api-keys)
- mod.io API key (optional) — [Get yours here](https://mod.io/me/access)

---

## Installation

1. Download the latest release from the [Releases](../../releases) page
2. Extract and run `BG3ModHelper.exe`
3. On first launch, enter your API key(s) in Settings

---

## Links

- **Nexus Mods page:** https://www.nexusmods.com/baldursgate3/mods/TODO
- **Discord:** https://discord.gg/Pj4UksjUQs

---

## Credits

- [Norbyte](https://github.com/Norbyte) — [LSLib](https://github.com/Norbyte/lslib) (MIT) — `.pak` file parsing

See [THIRD_PARTY_LICENSES.md](THIRD_PARTY_LICENSES.md) for all third-party licenses.

---

## License

This project is licensed under the **GNU General Public License v3.0**.  
See [LICENSE](LICENSE) for details.
