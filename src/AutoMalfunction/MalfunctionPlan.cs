namespace AutoMalfunction
{
    /// <summary>What the automator should do on this tick.</summary>
    internal enum MalfStep
    {
        /// <summary>The game won't take an action right now (busy, blocked, too soon). Try later.</summary>
        Wait,

        /// <summary>
        /// The player doesn't know the weapon is malfunctioned yet. Press "check chamber / fix
        /// malfunction" once, which in vanilla only makes the player aware (no animation).
        /// </summary>
        MakeAware,

        /// <summary>Aware, type unknown: press "inspect weapon", which plays the malfunction inspection.</summary>
        Inspect,

        /// <summary>Type known: press "check chamber / fix malfunction", which plays the repair.</summary>
        Clear,

        /// <summary>The weapon is no longer malfunctioned. The sequence is over.</summary>
        Done,

        /// <summary>
        /// The same step was issued too often without the state moving on. Stop, leave the
        /// malfunction to the player, and don't retry this malfunction.
        /// </summary>
        GiveUp,
    }

    /// <summary>
    /// The decision table, kept free of game types so tools/PlanTests can check it without the game.
    ///
    /// The order mirrors the vanilla state machine in Player.FirearmController.Idling:
    ///   - CheckChamber() with an unknown malfunction calls InventoryController.ExamineMalfunction (aware).
    ///   - ExamineWeapon() with a known malfunction starts ExamineMalfunctionTypeOperation, whose
    ///     OnMalfunctionOffEvent calls ExamineMalfunctionType (type known).
    ///   - CheckChamber() with a known type starts RepairMalfunction, which calls MalfState.Repair()
    ///     in its Start(), so the state reads None from the first frame of the repair animation.
    /// </summary>
    internal static class MalfunctionPlan
    {
        /// <summary>Times each step may be issued for one malfunction before giving up.</summary>
        internal const int MaxIssuesPerStep = 3;

        internal static MalfStep Decide(
            bool malfunctioned,
            bool knowsMalfunction,
            bool knowsType,
            bool gameAllowsAction,
            int awareIssued,
            int inspectIssued,
            int clearIssued)
        {
            if (!malfunctioned)
            {
                return MalfStep.Done;
            }

            MalfStep wanted;
            int issued;
            if (!knowsMalfunction)
            {
                wanted = MalfStep.MakeAware;
                issued = awareIssued;
            }
            else if (!knowsType)
            {
                wanted = MalfStep.Inspect;
                issued = inspectIssued;
            }
            else
            {
                wanted = MalfStep.Clear;
                issued = clearIssued;
            }

            if (issued >= MaxIssuesPerStep)
            {
                return MalfStep.GiveUp;
            }
            return gameAllowsAction ? wanted : MalfStep.Wait;
        }
    }
}
