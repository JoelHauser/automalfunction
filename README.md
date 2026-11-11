# Auto Malfunction

An SPT client mod. When your weapon malfunctions, your character inspects it and clears it on their own. You don't press anything.

It uses the game's own inspection and repair, with the same animations, sounds and durations. The Troubleshooting skill, broken arms and hard-slide repair times all apply as usual. You lose the same time to a malfunction as in vanilla; you just don't press the keys.

Built for SPT 4.x on EFT 0.16.9.40743.

## What happens

1. You fire and the weapon malfunctions. The game plays its malfunction animation as usual.
2. When the weapon is back at rest, your character inspects it (the same as pressing **Inspect weapon**).
3. When the inspection ends, your character clears it (the same as pressing **Check chamber / Fix malfunction**).
4. You have the weapon back. Firing again needs a fresh trigger pull; the mod never fires for you.

If the type is already known (you inspected earlier, or Troubleshooting elite identifies it instantly), step 2 is skipped.

All five malfunction types are handled: misfire, failure to eject (jam), failure to feed, and soft and hard slide.

## When it waits

It waits instead of acting while:

- the trigger is held
- the inventory is open
- you're mounted or on a stationary weapon (vanilla would pull you off; the mod leaves that to you)
- blind firing, a left-hand action, or another weapon action is in progress

It stops for good on that malfunction if you switch or drop the weapon, die, or leave the raid. If you inspect or clear it yourself first, the mod sees that and does nothing extra.

## Settings (F12)

- **Clear malfunctions automatically**: on by default. Switching it off mid-sequence lets an inspection or repair that's already playing finish, then nothing more happens.
- **Debug logging** (advanced): logs each step to `BepInEx/LogOutput.log`, for bug reports.

## Install

Unzip over your SPT folder. That puts `BepInEx/plugins/AutoMalfunction.dll` in place.

Don't combine it with mods that skip inspection (InspectionlessMalfs and the like). They still work together, but the mod then always goes straight to clearing.
