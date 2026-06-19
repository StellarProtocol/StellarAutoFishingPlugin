# StellarAutoFishingPlugin

A [Stellar Framework](https://github.com/) plugin that fully automates the fishing minigame — from equipping a rod to reeling in the catch — so you can leave it running unattended.

## Features

- **Full loop automation** — handles every stage: rod equip → cast → bite detection → hook → QTE reel → catch → repeat
- **Auto rod change** — automatically selects and equips the highest-quality fishing rod from your bag when the rod slot is empty
- **Tension control** — configurable high/low thresholds for the QTE reel phase; hold and release the rod at the right moment to keep tension in the safe zone
- **Monthly card interrupt** — detects and closes the daily login card and item reward popups, then automatically resumes fishing after a short delay
- **Session stats** — tracks fish caught, fish missed, and rod swaps in the current session

## Requirements

- [Stellar Framework](https://github.com/) installed in the game directory

## Installation

Copy `Stellar.AutoFishing.dll` into:

```
<GameDir>\stellar\plugins\autofishing\
```

The plugin loads automatically when the game starts.

## Usage

1. Enter a fishing area and interact with a fishing spot to open the fishing UI
2. Open the Stellar launcher overlay and click **Auto Fishing**
3. Click **Start** — the plugin takes over from here

Click **Stop** at any time to return to manual control.

## Configuration

Settings are saved automatically to `stellar.autofishing.config.json` in the game directory.

| Setting | Default | Description |
|---|---|---|
| Auto Rod Change | On | Equip the best rod from bag when none is equipped |
| Monthly Card Interrupt | On | Auto-close the daily login popup and resume |
| Release at (tension %) | 25 | Release the rod when tension reaches this threshold |
| Pull at (tension %) | 8 | Hold the rod again when tension drops to this threshold |

## License

Copyright (C) 2026 speedxpz

Licensed under the [GNU Affero General Public License v3.0](LICENSE).
