# StellarAutoFishingPlugin

A [Stellar Framework](https://github.com/) plugin for **Blue Protocol Star Resonance** that fully automates the fishing minigame — from equipping a rod to reeling in the catch — so you can leave it running unattended.

## Features

- **Full loop automation** — handles every stage: rod equip → cast → bite detection → hook → QTE reel → catch → repeat
- **Auto rod change** — automatically selects and equips the highest-quality fishing rod from your bag when the rod slot is empty
- **Tension control** — configurable high/low thresholds for the QTE reel phase; hold and release the rod at the right moment to keep tension in the safe zone
- **Monthly card interrupt** — detects and closes the daily login card and item reward popups, then automatically resumes fishing after a short delay
- **Session stats** — tracks fish caught, fish missed, and rod swaps in the current session

## Requirements

- Blue Protocol Star Resonance (`release_2.11` or compatible)
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

This program is free software: you can redistribute it and/or modify it under the terms of the **GNU Affero General Public License** as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later version.

This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the [GNU Affero General Public License](https://www.gnu.org/licenses/agpl-3.0.html) for more details.
