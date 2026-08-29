# Auto Party Finder

Dalamud plugin based on [goatcorp/SamplePlugin](https://github.com/goatcorp/SamplePlugin). It posts **Recruit Members** Party Finder listings from saved conditions, then puts the listing back up if it ends.

## What it does

The `/apf` window matches the in-game Recruit Members form:

- Party type: Normal / Alliance / Custom Match
- Duty category (None, Duty Roulette, Dungeons, Guildhests, Trials, Raids, High-end Duty, PvP, Gold Saucer, FATEs, Treasure Hunt, The Hunt, Gathering Forays, Deep Dungeons, Field Operations, V&C Dungeon Finder)
- Duty (filtered list for that category)
- Objective: None / Duty Completion / Practice / Loot
- Newbie welcome
- Comment
- Avg. Item Lv. (0–999)
- Auto put up PF
- Close Party Finder when queueing for a duty

Conditions are saved automatically. **Load last in-game PF** copies whatever you last set in the native Party Finder window.

If **Auto put up PF** is ticked, the listing is posted again after **62 minutes**, or as soon as Party Finder ends for any other reason.

**Close Party Finder when queueing for a duty** (on by default) closes the Party Finder windows when you enter a Duty Finder or Raid Finder queue. Auto-relist waits until you leave the queue or duty before opening Party Finder again.

## Commands

| Command | Action |
| --- | --- |
| `/apf` | Open the recruiter |
| `/apf recruit` | Post using saved conditions |
| `/apf stop` | Stop auto-relisting (keeps the current listing) |
| `/apf end` | Stop auto-relisting and end the current listing |

## Build

```bash
export PATH="$HOME/.dotnet:$PATH"
export DALAMUD_HOME="$HOME/.xlcore/dalamud/Hooks/dev"
dotnet build AutoPartyFinder.slnx -c Release
```

## Install (dev)

In-game `/xlsettings` → **Experimental** → **Dev Plugin Locations**, add:

```
Z:\home\deadlydeathangel\Projects\AutoPartyFinder\AutoPartyFinder\bin\Release\AutoPartyFinder.dll
```

You must be logged in. The plugin opens the real Party Finder UI and clicks **Recruit Members**, so the listing is created the same way as doing it by hand.
