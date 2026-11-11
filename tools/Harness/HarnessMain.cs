using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using AutoMalfunction;
using BepInEx.Configuration;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;

/// <summary>
/// Runs in the game's mono runtime (tools/MonoHost). Two parts:
///
/// 1. The real MalfunctionSequence drives a SimWeapon: the game's own Weapon.MalfunctionState
///    object, plus a model of Player.FirearmController.Idling / ExamineMalfunctionTypeOperation /
///    RepairMalfunction as read from the decompiled 0.16.9.40743 assembly. The model is our
///    reading of that code, not the code itself: it proves the mod's logic, not the game's.
/// 2. The Harmony patch on the real Idling.Start applies in this runtime, and its postfix
///    returns quietly for the cases it must ignore.
/// </summary>
internal static class HarnessMain
{
    private static int _failures;

    private static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
        if (!ok)
        {
            _failures++;
        }
    }

    private static int Main(string[] args)
    {
        string outDir = args.Length > 0 ? args[0] : Path.GetTempPath();
        // ConfigFile's static initializer reads BepInEx.Paths, which the preloader normally sets.
        string fakeRoot = Path.Combine(outDir, "fakegame");
        Directory.CreateDirectory(Path.Combine(fakeRoot, "BepInEx", "config"));
        MethodInfo setPath = typeof(BepInEx.Paths).GetMethod("SetExecutablePath", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        object[] pathArgs = new object[setPath.GetParameters().Length];
        pathArgs[0] = Path.Combine(fakeRoot, "Game.exe");
        setPath.Invoke(null, pathArgs);
        Plugin.Enabled = new ConfigFile(Path.Combine(outDir, "harness.cfg"), true).Bind("1. General", "Enabled", true, "");
        Plugin.DebugLogging = null;
        Plugin.Log = BepInEx.Logging.Logger.CreateLogSource("harness");

        Console.WriteLine("Decision table");
        PlanTable();

        Console.WriteLine("Sequences on the game's MalfunctionState");
        NewMalfunctionEveryType();
        AlreadyIdentified();
        UnknownMalfunction();
        TriggerHeldWaits();
        ManualInspectDuringPending();
        ManualClearDuringPending();
        WeaponSwitchedDuringInspection();
        DeathDuringSequence();
        DisabledDuringPending();
        DisabledDuringInspection();
        RepeatedMalfunctions();
        InspectionThatNeverIdentifies();
        NewMalfunctionAfterGiveUp();
        GameRefusesEveryPress();
        InventoryOpenWaits();

        Console.WriteLine("Harmony patch in the game runtime");
        PatchTest();

        Console.WriteLine(_failures == 0 ? "ALL PASSED" : _failures + " FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- decision table

    private static void PlanTable()
    {
        Check(MalfunctionPlan.Decide(false, true, true, true, 0, 0, 0) == MalfStep.Done, "no malfunction: done");
        Check(MalfunctionPlan.Decide(true, false, false, true, 0, 0, 0) == MalfStep.MakeAware, "unknown: make aware");
        Check(MalfunctionPlan.Decide(true, true, false, true, 0, 0, 0) == MalfStep.Inspect, "aware, type unknown: inspect");
        Check(MalfunctionPlan.Decide(true, true, true, true, 0, 0, 0) == MalfStep.Clear, "type known: clear");
        Check(MalfunctionPlan.Decide(true, true, false, false, 0, 0, 0) == MalfStep.Wait, "blocked: wait");
        Check(MalfunctionPlan.Decide(true, true, false, true, 0, MalfunctionPlan.MaxIssuesPerStep, 0) == MalfStep.GiveUp, "inspect cap: give up");
        Check(MalfunctionPlan.Decide(true, true, true, false, 0, 0, MalfunctionPlan.MaxIssuesPerStep) == MalfStep.GiveUp, "clear cap gives up even while blocked");
        Check(MalfunctionPlan.Decide(true, true, true, true, 9, 9, 0) == MalfStep.Clear, "other steps' counts don't block clearing");
    }

    // ---------------------------------------------------------------- simulated weapon

    private enum Op { Idle, Inspecting, Repairing, Utility }

    /// <summary>
    /// The game's MalfunctionState plus a model of the hands operations. Timings are made up;
    /// only the order of state changes matters. Every place the game would call Idling.Start
    /// calls OnIdle, which is what IdlingStartPatch does.
    /// </summary>
    private sealed class SimWeapon : IMalfunctionHost
    {
        internal const string Me = "me";
        internal const float InspectSeconds = 1.6f;
        internal const float RepairSeconds = 2.2f;

        internal readonly Weapon.MalfunctionState Malf = new Weapon.MalfunctionState();
        internal Op Op = Op.Idle;
        internal float OpLeft;
        internal bool TriggerHeld, Dead, Switched, InventoryOpen, RefuseEverything, InspectionNeverIdentifies;
        internal readonly List<string> Log = new List<string>();
        internal int InspectAnimations, RepairAnimations;

        internal Action OnIdle = () => { };

        /// <summary>FireOperation path: malfunction, then ExamineMalfunction(clearRest: true), then StartMalfunction.</summary>
        internal void MalfunctionOnShot(Weapon.EMalfunctionState state, float now)
        {
            Malf.State = state;
            Malf.LastMalfunctionTime = now;
            Malf.AddPlayerWhoKnowMalfunction(Me, clearRest: true);
            OnIdle(); // StartMalfunction.OnMalfunctionOffEvent -> SwitchToIdle -> Idling.Start
        }

        internal void Advance(float dt)
        {
            if (Op == Op.Idle)
            {
                return;
            }
            OpLeft -= dt;
            if (OpLeft > 0)
            {
                return;
            }
            Op finished = Op;
            Op = Op.Idle;
            if (finished == Op.Inspecting && !InspectionNeverIdentifies)
            {
                // ExamineMalfunctionTypeOperation.OnMalfunctionOffEvent
                Malf.AddPlayerWhoKnowMalfType(Me);
            }
            Log.Add("end " + finished);
            OnIdle();
        }

        public string StaleReason()
        {
            if (!Plugin.Enabled.Value) return "mod disabled";
            if (Dead) return "player dead";
            if (Switched) return "hands changed";
            return null;
        }

        public bool Malfunctioned => Malf.State != Weapon.EMalfunctionState.None;
        public bool KnowsMalfunction => Malf.IsKnownMalfunction(Me);
        public bool KnowsType => Malf.IsKnownMalfType(Me);

        public bool GameAllowsAction() => Op == Op.Idle && !TriggerHeld && !InventoryOpen;

        /// <summary>Idling.ExamineWeapon / Idling.CheckChamber, as decompiled.</summary>
        public bool Press(MalfStep step)
        {
            Log.Add("press " + step);
            if (RefuseEverything || Op != Op.Idle)
            {
                return false;
            }
            if (step == MalfStep.Inspect)
            {
                Weapon.EMalfunctionState s = Malf.State;
                bool slideOrMisfire = s == Weapon.EMalfunctionState.Misfire || s == Weapon.EMalfunctionState.SoftSlide || s == Weapon.EMalfunctionState.HardSlide;
                if (slideOrMisfire && !Malf.IsKnownMalfunction(Me))
                {
                    Op = Op.Utility; // plain look at the weapon, learns nothing
                    OpLeft = 1f;
                    return true;
                }
                Op = Op.Inspecting;
                OpLeft = InspectSeconds;
                InspectAnimations++;
                return true;
            }
            if (TriggerHeld)
            {
                return false;
            }
            if (Malf.IsKnownMalfType(Me))
            {
                Op = Op.Repairing; // RepairMalfunction.Start calls MalfState.Repair()
                OpLeft = RepairSeconds;
                RepairAnimations++;
                Malf.Repair();
                return true;
            }
            Malf.AddPlayerWhoKnowMalfunction(Me); // InventoryController.ExamineMalfunction
            return true;
        }

        /// <summary>The player presses a key themselves.</summary>
        internal void ManualPress(MalfStep step)
        {
            Press(step);
        }
    }

    /// <summary>Plays the role of MalfunctionAutomator: enabled while a sequence is pending.</summary>
    private sealed class Driver
    {
        internal readonly SimWeapon W;
        internal TickResult? Ended;
        internal float Now;
        internal int Frame;
        internal readonly List<float> PressTimes = new List<float>();
        internal readonly List<int> WakeFrames = new List<int>();
        internal readonly List<int> PressFrames = new List<int>();
        internal int SequencesStarted;
        private int _pressesSeen;

        internal Driver(SimWeapon w)
        {
            W = w;
            w.OnIdle = OnIdle;
        }

        internal readonly SequenceSlot Slot = new SequenceSlot();
        internal MalfunctionSequence Seq => Slot.Current;

        /// <summary>IdlingStartPatch.Postfix + MalfunctionAutomator.Arm, with the real SequenceSlot.</summary>
        private void OnIdle()
        {
            if (!Plugin.Enabled.Value || !W.Malfunctioned)
            {
                return;
            }
            if (Slot.Arm(this, W, W.Malf.LastMalfunctionTime, Now, Frame, out bool started))
            {
                WakeFrames.Add(Frame);
                if (started)
                {
                    Ended = null;
                    SequencesStarted++;
                }
            }
        }

        internal void Run(float seconds, Action<float> each = null)
        {
            const float dt = 1f / 60f;
            float end = Now + seconds;
            while (Now < end)
            {
                Now += dt;
                Frame++;
                each?.Invoke(Now);
                W.Advance(dt);
                if (Seq != null)
                {
                    TickResult r = Seq.Tick(W, Now, Frame);
                    if (Seq.Presses > _pressesSeen)
                    {
                        _pressesSeen = Seq.Presses;
                        PressTimes.Add(Now);
                        PressFrames.Add(Frame);
                    }
                    if (r != TickResult.Continue)
                    {
                        Ended = r;
                        Slot.End(r, W.Malf.LastMalfunctionTime);
                        _pressesSeen = 0;
                    }
                }
            }
        }

        internal int Presses(string step) => W.Log.Count(l => l == "press " + step);
    }

    private static bool SpacedOut(Driver d) =>
        d.PressTimes.Zip(d.PressTimes.Skip(1), (a, b) => b - a).All(gap => gap >= MalfunctionSequence.RetrySeconds - 0.001f);

    private static bool NeverInWakeFrame(Driver d) => !d.PressFrames.Intersect(d.WakeFrames).Any();

    // ---------------------------------------------------------------- scenarios

    private static void NewMalfunctionEveryType()
    {
        foreach (Weapon.EMalfunctionState state in Enum.GetValues(typeof(Weapon.EMalfunctionState)))
        {
            if (state == Weapon.EMalfunctionState.None)
            {
                continue;
            }
            var d = new Driver(new SimWeapon());
            d.W.MalfunctionOnShot(state, d.Now);
            d.Run(8f);
            Check(d.Ended == TickResult.Done && !d.W.Malfunctioned
                  && d.W.InspectAnimations == 1 && d.W.RepairAnimations == 1
                  && d.Presses("Inspect") == 1 && d.Presses("Clear") == 1 && d.Presses("MakeAware") == 0,
                $"new {state}: one inspection, then one repair, then done ({string.Join(", ", d.W.Log)})");
            int inspectEnd = d.W.Log.IndexOf("end Inspecting");
            int clear = d.W.Log.IndexOf("press Clear");
            Check(inspectEnd >= 0 && clear > inspectEnd, $"new {state}: repair pressed only after the inspection finished");
            Check(SpacedOut(d) && NeverInWakeFrame(d), $"new {state}: presses spaced out, never in the idle-start frame");
        }
    }

    private static void AlreadyIdentified()
    {
        var d = new Driver(new SimWeapon());
        d.W.Malf.State = Weapon.EMalfunctionState.Jam;
        d.W.Malf.AddPlayerWhoKnowMalfType(SimWeapon.Me); // inspected earlier, or Troubleshooting elite
        d.W.OnIdle(); // weapon drawn: SpawnOperation -> Idling.Start
        d.Run(5f);
        Check(d.Ended == TickResult.Done && d.W.InspectAnimations == 0 && d.W.RepairAnimations == 1 && d.Presses("Inspect") == 0,
            "already identified: straight to the repair, no inspection");
    }

    private static void UnknownMalfunction()
    {
        var d = new Driver(new SimWeapon());
        d.W.Malf.State = Weapon.EMalfunctionState.SoftSlide; // e.g. a weapon picked up mid-malfunction
        d.W.OnIdle();
        d.Run(8f);
        Check(d.Ended == TickResult.Done && d.Presses("MakeAware") == 1 && d.W.InspectAnimations == 1 && d.W.RepairAnimations == 1
              && d.W.Log.Count(l => l == "end Utility") == 0,
            "unknown malfunction: aware first (so inspection isn't a plain weapon look), then inspect, then clear");
    }

    private static void TriggerHeldWaits()
    {
        var d = new Driver(new SimWeapon());
        d.W.TriggerHeld = true;
        d.W.MalfunctionOnShot(Weapon.EMalfunctionState.Jam, d.Now);
        d.Run(2f);
        Check(d.W.Log.Count(l => l.StartsWith("press")) == 0 && d.Seq != null, "trigger held: no presses, still pending");
        d.W.TriggerHeld = false;
        d.Run(6f);
        Check(d.Ended == TickResult.Done && d.W.RepairAnimations == 1, "trigger released: carries on and clears");
    }

    private static void ManualInspectDuringPending()
    {
        var d = new Driver(new SimWeapon());
        d.W.MalfunctionOnShot(Weapon.EMalfunctionState.Feed, d.Now);
        d.W.ManualPress(MalfStep.Inspect); // player hits inspect before the settle delay ends
        d.Run(6f);
        Check(d.Ended == TickResult.Done && d.W.InspectAnimations == 1 && d.W.RepairAnimations == 1,
            "player inspects first: no second inspection, we clear after theirs");
        Check(d.W.Log.Count(l => l == "press Inspect") == 1, "the only inspect press is the player's");
    }

    private static void ManualClearDuringPending()
    {
        var d = new Driver(new SimWeapon());
        d.W.Malf.State = Weapon.EMalfunctionState.Misfire;
        d.W.Malf.AddPlayerWhoKnowMalfType(SimWeapon.Me);
        d.W.OnIdle();
        d.W.ManualPress(MalfStep.Clear); // player fixes it in the same frame
        d.Run(4f);
        Check(d.Ended == TickResult.Done && d.W.RepairAnimations == 1 && d.Presses("Clear") == 1,
            "player clears first: we see it fixed and press nothing");
    }

    private static void WeaponSwitchedDuringInspection()
    {
        var d = new Driver(new SimWeapon());
        d.W.MalfunctionOnShot(Weapon.EMalfunctionState.HardSlide, d.Now);
        d.Run(0.5f); // inspection started
        d.W.Switched = true;
        d.Run(6f);
        Check(d.Ended == TickResult.Cancelled && d.W.RepairAnimations == 0 && d.Presses("Clear") == 0,
            "weapon switched mid-inspection: cancelled, no repair pressed on the next weapon");
    }

    private static void DeathDuringSequence()
    {
        var d = new Driver(new SimWeapon());
        d.W.MalfunctionOnShot(Weapon.EMalfunctionState.Jam, d.Now);
        d.Run(0.5f);
        d.W.Dead = true;
        d.Run(5f);
        Check(d.Ended == TickResult.Cancelled && d.Presses("Clear") == 0, "death: cancelled, nothing pressed after");
    }

    private static void DisabledDuringPending()
    {
        var d = new Driver(new SimWeapon());
        d.W.TriggerHeld = true;
        d.W.MalfunctionOnShot(Weapon.EMalfunctionState.Jam, d.Now);
        d.Run(0.5f);
        Plugin.Enabled.Value = false;
        d.W.TriggerHeld = false;
        d.Run(5f);
        Check(d.Ended == TickResult.Cancelled && d.W.Log.Count(l => l.StartsWith("press")) == 0 && d.W.Malfunctioned,
            "disabled while waiting: cancelled, nothing pressed, malfunction left to the player");
        Plugin.Enabled.Value = true;
    }

    private static void DisabledDuringInspection()
    {
        var d = new Driver(new SimWeapon());
        d.W.MalfunctionOnShot(Weapon.EMalfunctionState.Jam, d.Now);
        d.Run(0.5f); // our inspection is playing
        Plugin.Enabled.Value = false;
        d.Run(5f);
        Check(d.W.InspectAnimations == 1 && d.W.RepairAnimations == 0 && d.W.KnowsType && d.Ended == TickResult.Cancelled,
            "disabled mid-inspection: the inspection finishes as vanilla, no repair follows");
        Plugin.Enabled.Value = true;
    }

    private static void RepeatedMalfunctions()
    {
        var d = new Driver(new SimWeapon());
        for (int i = 0; i < 3; i++)
        {
            d.W.MalfunctionOnShot(i % 2 == 0 ? Weapon.EMalfunctionState.Jam : Weapon.EMalfunctionState.Misfire, d.Now);
            d.Run(7f);
        }
        Check(d.SequencesStarted == 3 && d.W.InspectAnimations == 3 && d.W.RepairAnimations == 3 && d.Ended == TickResult.Done,
            "three malfunctions in a row: each gets its own fresh sequence");
    }

    private static void InspectionThatNeverIdentifies()
    {
        var d = new Driver(new SimWeapon { InspectionNeverIdentifies = true });
        d.W.MalfunctionOnShot(Weapon.EMalfunctionState.Jam, d.Now);
        d.Run(30f);
        Check(d.Ended == TickResult.GaveUp && d.W.InspectAnimations == MalfunctionPlan.MaxIssuesPerStep,
            $"inspection that never identifies: gives up after {MalfunctionPlan.MaxIssuesPerStep}, no endless loop (ended {d.Ended}, {d.W.InspectAnimations} inspections)");
    }

    private static void NewMalfunctionAfterGiveUp()
    {
        var d = new Driver(new SimWeapon { InspectionNeverIdentifies = true });
        d.W.MalfunctionOnShot(Weapon.EMalfunctionState.Jam, d.Now);
        d.Run(30f);
        d.W.InspectionNeverIdentifies = false;
        d.W.OnIdle(); // the player reloads or checks ammo: idle again on the same malfunction
        d.Run(5f);
        Check(d.SequencesStarted == 1 && d.Ended == TickResult.GaveUp, "same malfunction going idle again after a give-up: stays given up");
        d.W.Malf.Repair();
        d.W.MalfunctionOnShot(Weapon.EMalfunctionState.Feed, d.Now); // fixed by the player, then a new one
        d.Run(8f);
        Check(d.SequencesStarted == 2 && d.Ended == TickResult.Done, "a new malfunction on that weapon is handled again");
    }

    private static void GameRefusesEveryPress()
    {
        var d = new Driver(new SimWeapon { RefuseEverything = true });
        d.W.MalfunctionOnShot(Weapon.EMalfunctionState.Jam, d.Now);
        d.Run(30f);
        int presses = d.W.Log.Count(l => l.StartsWith("press"));
        Check(d.Ended == TickResult.GaveUp && presses == MalfunctionSequence.MaxRefusedInARow && SpacedOut(d),
            $"game refuses every press: {presses} spaced presses, then stops");
    }

    private static void InventoryOpenWaits()
    {
        var d = new Driver(new SimWeapon());
        d.W.InventoryOpen = true;
        d.W.MalfunctionOnShot(Weapon.EMalfunctionState.Feed, d.Now);
        d.Run(3f);
        Check(d.W.Log.Count(l => l.StartsWith("press")) == 0, "inventory open: waits");
        d.W.InventoryOpen = false;
        d.Run(6f);
        Check(d.Ended == TickResult.Done, "inventory closed: clears");
    }

    // ---------------------------------------------------------------- patch

    private static void PatchTest()
    {
        MethodInfo target = AccessTools.Method(typeof(Player.FirearmController.Idling), nameof(Player.FirearmController.Idling.Start), new[] { typeof(Action) });
        Check(target != null && target.DeclaringType == typeof(Player.FirearmController.Idling), "Idling.Start(Action) resolves on Idling itself");

        var harmony = new Harmony("automalfunction.harness");
        try
        {
            harmony.PatchAll(typeof(Plugin).Assembly);
            var info = Harmony.GetPatchInfo(target);
            Check(info != null && info.Postfixes.Any(p => p.owner == "automalfunction.harness"), "PatchAll puts the postfix on Idling.Start");
        }
        catch (Exception e)
        {
            Check(false, "PatchAll threw: " + e.GetBaseException().Message);
        }

        // A bare Weapon can't be made here: Item's static initializer calls into Unity, which
        // isn't loaded. So only the paths before the weapon is read are run.
        MethodInfo postfix = typeof(IdlingStartPatch).GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static);
        var idling = (Player.FirearmController.Idling)FormatterServices.GetUninitializedObject(typeof(Player.FirearmController.Idling));
        bool quiet = true;
        try
        {
            postfix.Invoke(null, new object[] { idling }); // no weapon
            Plugin.Enabled.Value = false;
            postfix.Invoke(null, new object[] { idling }); // mod off
            Plugin.Enabled.Value = true;
        }
        catch (Exception e)
        {
            quiet = false;
            Console.WriteLine("    " + e.GetBaseException());
        }
        Check(quiet && MalfunctionAutomator.Instance == null, "postfix returns quietly with no weapon and with the mod off");
        harmony.UnpatchSelf();
    }
}
