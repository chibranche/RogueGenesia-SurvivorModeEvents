# Survivor Mode Events

A mod that triggers Rog mode events at a configurable interval during Survivor mode runs.

- **Adjustable interval** (1 to 60 minutes, default 5).
- **Per-event toggle** — enable only the ones you want.
- **Auto-pause** while the event is open, so you can read and choose without dying.
- **Skipped during Overdrive** — no events fire after the final boss is killed and
  the timer goes wild.
- **Modded events auto-detected** — events from other mods are discovered at startup,
  filtered through a safety probe, and offered as opt-in toggles (default off).

## Settings

A new **Survivor Mode Events** tab is added to the in-game options menu:

| Setting | Range | Default |
|---|---|---|
| Event Interval (minutes) | 1 → 60 | **5** |
| 19 vanilla event toggles | on/off | all **on** |
| Modded / unknown event toggles | on/off | all **off** (opt-in) |

## Supported vanilla events

Hand-curated as safe in Survivor mode (no stage map, no scene transition, no combat
node load). Each one is purely persistent-data or in-event — safe to fire mid-run:

Banker · Bard · Bonfire · Gambling · God Statuette · God Stele · Golden Altar ·
Jaald Temple · Priest · Rogue Goblin · Shopkeeper Investment ·
Shrine of Balance / Disorder / Elemental Order / Order / Reconstruction ·
Soul Forge · Trainer · Wandering Caravan

Combat-style events (Magic Mirror, all Notice Board variants, Save Villagers, plus any
modded events) are auto-blocked via the vanilla `ICombatEvent` marker interface — they
would terminate the run by loading a separate battle scene. A small extra hand-list
catches the remaining non-combat broken/placeholder events (Crystal Mine, Default,
Legacy).

## Modded events

Events from other mods are picked up automatically at startup, as long as the mod
registers them via the vanilla `EventManager.RegisterEvent(...)` call (the same path
vanilla uses for its own events). Sub-events that aren't registered as primary
triggerables are skipped, so the settings UI doesn't show internal branch classes.

Each candidate goes through a safety probe before getting a toggle:

1. **Constructor check** — `Activator.CreateInstance` must succeed.
2. **IL scan of `OnLevelLoaded` / `NextAnswer`** — if the method directly calls
   `SceneManager.LoadScene` / `LoadSceneAsync` for any scene other than the additive
   `Event` scene, the event is rejected as combat-style. (Events that use the normal
   `EventManager.NextStage` flow are fine — we patch that path.)

Events that pass the probe are offered as toggles labelled `[Modded] <ClassName>` (or
`[New] <ClassName>` for unrecognized vanilla events that may have been added in a game
update). They default to **off** so you opt in explicitly.

Even after passing the probe, a wildcard Harmony finalizer wraps every offered event's
`OnLevelLoaded`. If a modded event's setup throws at runtime while we're driving the
flow, the finalizer logs the error and aborts the event cleanly instead of leaving the
player stuck.

**Limitations** — the IL scan only inspects top-level instructions of `OnLevelLoaded`
and `NextAnswer`. A modded event that hides its scene load behind a helper method, or
loads a scene by build-index instead of by name, will pass the probe but may still
break the run. Disable the toggle if you observe issues.

The startup log (`Player.log`) prints a discovery summary (`safe / new / modded /
blocked / probe-failed`) and the reason for each rejection.

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
├── Plugin.cs                    Mod entry, option registration, wildcard finalizer wiring
├── EventDiscovery.cs            Reflection scan, vanilla safelist/blocklist, IL safety probe
├── EventTriggerLoop.cs          Timer + scene load/unload logic
├── Patches.cs                   Harmony patches (Survival.Update/Start/OnPlayerDeath, EventManager.NextStage*, BlackFader/WhiteFader.Awake, BankerEvent recovery)
└── ModInfo.rgmod                Mod metadata read by the game
```

## Known caveats

- **Same event back-to-back**: the random pick is uniform with no anti-repeat — you
  may occasionally see the same event fire two intervals in a row.