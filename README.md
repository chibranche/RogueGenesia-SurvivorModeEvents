# Survivor Mode Events

A mod that triggers Rog mode events at a configurable interval during Survivor mode runs.

- **Adjustable interval** (1 to 60 minutes, default 5).
- **Per-event toggle** for all 20 supported events — enable only the ones you want.
- **Auto-pause** while the event is open, so you can read and choose without dying.
- **Skipped during Overdrive** — no events fire after the final boss is killed and
  the timer goes wild.

## Settings

A new **Survivor Mode Events** tab is added to the in-game options menu:

| Setting | Range | Default |
|---|---|---|
| Event Interval (minutes) | 1 → 60 | **5** |
| ...and 20 toggles, one per event | on/off | all **on** |

## Supported events

Only events that have no Rog-mode-specific dependencies (no stage map, no scene
transition, no combat node load) are included. Each one is purely persistent-data
or in-event — safe to fire mid-run:

Banker · Bard · Bonfire · Gambling · God Statuette · God Stele · Golden Altar ·
Jaald Temple · Legacy · Priest · Rogue Goblin · Shopkeeper Investment ·
Shrine of Balance / Disorder / Elemental Order / Order / Reconstruction ·
Soul Forge · Trainer · Wandering Caravan

Combat-style events (Magic Mirror, Notice Board challenges, Save Villagers'
fight branch) are intentionally excluded — they would terminate the current
Survivor run by loading a separate combat scene.

## How it works

A Harmony postfix on `GameManagerSurvival.Update` ticks each frame and tracks
`SurvivorsModeTiming`. When the elapsed time crosses the next interval boundary
and `GameData.ReachedInfinity` is false (i.e. you haven't entered Overdrive),
the mod:

1. Freezes the run with `Time.timeScale = 0f`.
2. Picks a random *enabled* event from the 20-event pool.
3. Additively loads the vanilla `Event` scene with `EventManager.NextEvent` pre-set.
4. The vanilla EventManager populates its UI as usual — the player picks an answer.
5. When the player clicks Continue, a Harmony prefix on `EventManager.Next_Stage_Confirm`
   detects the survivor flag, unloads the event scene, restores the time scale,
   and resumes the run instead of trying to load the next Rog stage (which doesn't
   exist in Survivor mode).

State is reset on every new Survivor run via a postfix on `GameManagerSurvival.Start`.

## Building

Requires:
- [.NET SDK 8.0+](https://dotnet.microsoft.com/download)
- A Rogue Genesia install on the **Modded** branch (the `Managed` folder must exist
  at `<game install>/Modded/Rogue Genesia_Data/Managed/`)
- Visual Studio 2022, or `dotnet build` from the command line

### Configure your game install path

The csproj defaults to `E:\SteamLibrary\steamapps\common\Rogue Genesia\Modded\...`.
If your install is elsewhere, set the `RogueGenesiaManaged` environment variable
before building. Persist it once with PowerShell:

```powershell
[Environment]::SetEnvironmentVariable(
  'RogueGenesiaManaged',
  'C:\Path\To\SteamLibrary\steamapps\common\Rogue Genesia\Modded\Rogue Genesia_Data\Managed',
  'User')
```

Then restart Visual Studio / your terminal so it picks up the variable.

Or pass it on the command line for a one-off build:

```powershell
dotnet build -c Release -p:RogueGenesiaManaged="C:\Path\To\Managed"
```

You can also override the install destination with `ModInstallFolder` if you don't
want the build to copy into the game's `Mods` folder.

### Build

In Visual Studio: **Build → Build Solution** (Ctrl+Shift+B).
From the command line: `dotnet build -c Release` from the repo root.

After a successful build, the mod files are copied automatically to
`<game install>/Modded/Mods/SurvivorModeEvents/`. Restart the game and enable
the mod from the in-game mod manager.

## Project layout

```
SurvivorModeEvents.sln
SurvivorModeEvents/
├── SurvivorModeEvents.csproj   Project + auto-install build target
├── Plugin.cs                    Mod entry, option registration, event whitelist
├── EventTriggerLoop.cs          Timer + scene load/unload logic
├── Patches.cs                   Harmony patches (Survival.Update, Survival.Start, EventManager.Next_Stage_Confirm)
└── ModInfo.rgmod                Mod metadata read by the game
```

## Known caveats

- **Same event back-to-back**: the random pick is uniform with no anti-repeat — you
  may occasionally see the same event fire two intervals in a row.
- **Visual layering**: the Event UI is drawn over the still-frozen Survivor scene.
  Functional but not pretty; iterate on this if it bothers you.
- **Pause interactions**: pressing Esc while an event is open hits the vanilla
  EventManager pause handler, which may behave unexpectedly combined with our
  forced `timeScale = 0`. Don't pause inside an event.
