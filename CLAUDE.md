# Auto Malfunction: working notes for Claude

SPT client mod: the local player's malfunctions get inspected and cleared automatically, through
the game's own FirearmController calls. Repo https://github.com/JoelHauser/automalfunction.git.

## State

0.1.0 (GitHub pre-release) built against EFT 0.16.9.40743 (SPT 4.x). Harness passes. **Never run in game.** The
in-game checklist is at the bottom.

## How the game does it (decompiled 0.16.9.40743, SPT-patched Assembly-CSharp)

- `Weapon.MalfunctionState` has two per-profile levels: `IsKnownMalfunction` (aware) and
  `IsKnownMalfType` (inspected). `Repair()` sets the state to None and clears both.
- Fire operations: on Jam/Feed/SoftSlide/HardSlide, and on Misfire, they call
  `InventoryController.ExamineMalfunction(weapon, clearRest: true)` (aware), then
  `StartMalfunction`, which ends in `SwitchToIdle` -> `Idling.Start`.
- `FirearmController.ExamineWeapon()` needs CurrentOperation `is Idling` and no hands action, then
  `Idling.ExamineWeapon()`: aware (or Jam/Feed) -> `ExamineMalfunctionTypeOperation`; its
  `OnMalfunctionOffEvent` calls `ExamineMalfunctionType` (type known) and goes back to Idling.
  Unaware Misfire/SoftSlide/HardSlide -> a plain UtilityOperation weapon look that learns nothing.
- `FirearmController.CheckChamber()` (no Idling check in the wrapper!) -> `Idling.CheckChamber()`:
  refuses while the trigger is held; type known -> `RepairMalfunction` (calls `MalfState.Repair()`
  in Start, so the state is None for the whole repair animation); unknown -> `ExamineMalfunction`
  only (aware, no animation).
- Troubleshooting elite: `ExamineMalfunction` also calls `ExamineMalfunctionType`, so the type is
  known straight away.
- Traps: `UtilityOperation` derives from `Idling` and answers `ExamineWeapon()` with true without
  doing anything, so the mod requires the exact Idling type. Both wrappers call
  `RemoveLeftHandItem` and leave mounted state before their checks, so the mod checks first.

## Code

- `IdlingStartPatch`: postfix on `Player.FirearmController.Idling.Start(Action)`, local player
  with a malfunction only. That's the only trigger; nothing runs per frame when idle.
- `MalfunctionAutomator`: MonoBehaviour, enabled only while pending. Implements `IMalfunctionHost`.
- `SequenceSlot` / `MalfunctionSequence` / `MalfunctionPlan`: game-free, tested by tools/Harness.

## Building on the work machine

`C:\HUH` is unpatched and must stay untouched. Build against a mirror (same recipe as
FirstShotSmoothness/CLAUDE.md): copy `BepInEx\core` and `EscapeFromTarkov_Data\Managed`, keep
`Assembly-CSharp.dll.spt-bak`, apply the SPT delta with SharpHDiffPatch.Core. Then from PowerShell:
`scripts\pack.ps1 -SPTPath <mirror>`.

## Harness

```
dotnet build tools\Harness\Harness.csproj -c Release -p:SPTPath=<mirror> -o <hbin>
dotnet build tools\MonoHost\MonoHost.csproj -c Release -o <mhost>
# <asm> = all of Managed + BepInEx\core + Harness.exe in one folder
<mhost>\MonoHost.exe C:\HUH\MonoBleedingEdge\EmbedRuntime C:\HUH\MonoBleedingEdge\etc <asm> <asm>\Harness.exe <outdir>
```

It runs the real sequence code against the game's real `Weapon.MalfunctionState`, with a model of
the hands operations written from the decompile. The model is our reading of the game, so a pass
proves the mod's logic, not the game's behaviour. It also patches the real `Idling.Start`.
A bare `Weapon` can't be made there (Item's static ctor calls Unity), and MonoBehaviours can't run.

## In-game checklist (not done yet)

1. Debug logging on. Shoot a low-durability weapon until it malfunctions: inspection plays by
   itself, then the repair, then you can fire again with a new trigger pull.
2. Hold the trigger through the malfunction: nothing happens until you release.
3. Inspect manually before the mod does: no second inspection.
4. Switch weapons mid-inspection, then draw the malfunctioned weapon again: it carries on.
5. Turn the setting off mid-inspection: the inspection ends, no repair.
6. Die or extract with a sequence pending: no errors in the log.
7. Each type at least once (misfire, jam, feed, soft slide, hard slide), and a Feed with a magazine.
8. Mounted/bipod: waits until you unmount.

## Rules

- Release zips hold only `BepInEx/plugins/AutoMalfunction.dll`. Version in the csproj and Plugin.cs.
- Never synthesize a trigger pull or a shot. Never touch MalfunctionState directly; only the
  game's own operations change it.
