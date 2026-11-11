namespace AutoMalfunction
{
    /// <summary>What the sequence needs from the game. <see cref="MalfunctionAutomator"/> is the real one.</summary>
    internal interface IMalfunctionHost
    {
        /// <summary>Why the sequence no longer applies (weapon gone, player dead, mod off), or null.</summary>
        string StaleReason();

        bool Malfunctioned { get; }
        bool KnowsMalfunction { get; }
        bool KnowsType { get; }

        /// <summary>True when the matching key press would be taken right now.</summary>
        bool GameAllowsAction();

        /// <summary>Makes the press. Returns what the game's method returned.</summary>
        bool Press(MalfStep step);
    }

    internal enum TickResult
    {
        Continue,
        Done,
        Cancelled,
        GaveUp,
    }

    /// <summary>
    /// One malfunction, from arming to done. Game-free so tools/Harness can drive it with a
    /// simulated weapon and the game's real Weapon.MalfunctionState.
    /// </summary>
    internal sealed class MalfunctionSequence
    {
        /// <summary>Delay after idle starts before the first press, so the idle pose can settle.</summary>
        internal const float SettleSeconds = 0.1f;

        /// <summary>Gap after any press (taken or refused) before the next one. Stops input spam.</summary>
        internal const float RetrySeconds = 0.25f;

        /// <summary>
        /// Presses the game refused in a row, although every precondition we know of said yes.
        /// That means a block we don't model; stop rather than press every RetrySeconds forever.
        /// </summary>
        internal const int MaxRefusedInARow = 8;

        private int _refusedInARow;

        private int _armedFrame;
        private float _nextTryTime;

        internal int AwareIssued { get; private set; }
        internal int InspectIssued { get; private set; }
        internal int ClearIssued { get; private set; }
        internal int Presses { get; private set; }
        internal string LastReason { get; private set; }

        /// <summary>
        /// The weapon went idle again with this malfunction (first time, or after one of our
        /// operations, or after the player did something). Counters are kept, so a step the game
        /// keeps ignoring still runs into the give-up cap.
        /// </summary>
        internal void Wake(float now, int frame)
        {
            _armedFrame = frame;
            float earliest = now + SettleSeconds;
            if (_nextTryTime < earliest)
            {
                _nextTryTime = earliest;
            }
        }

        internal TickResult Tick(IMalfunctionHost host, float now, int frame)
        {
            string stale = host.StaleReason();
            if (stale != null)
            {
                LastReason = stale;
                return TickResult.Cancelled;
            }

            // Never act in the frame we were woken: Idling.Start may still be on the stack.
            if (frame == _armedFrame || now < _nextTryTime)
            {
                return TickResult.Continue;
            }

            bool malfunctioned = host.Malfunctioned;
            MalfStep step = MalfunctionPlan.Decide(
                malfunctioned,
                malfunctioned && host.KnowsMalfunction,
                malfunctioned && host.KnowsType,
                malfunctioned && host.GameAllowsAction(),
                AwareIssued,
                InspectIssued,
                ClearIssued);

            switch (step)
            {
                case MalfStep.Wait:
                    return TickResult.Continue;
                case MalfStep.Done:
                    LastReason = "cleared";
                    return TickResult.Done;
                case MalfStep.GiveUp:
                    LastReason = "the game kept refusing to move on";
                    return TickResult.GaveUp;
            }

            Presses++;
            if (!host.Press(step))
            {
                _refusedInARow++;
                if (_refusedInARow >= MaxRefusedInARow)
                {
                    LastReason = "the game refused " + step + " " + _refusedInARow + " times in a row";
                    return TickResult.GaveUp;
                }
            }
            else
            {
                _refusedInARow = 0;
                switch (step)
                {
                    case MalfStep.MakeAware: AwareIssued++; break;
                    case MalfStep.Inspect: InspectIssued++; break;
                    case MalfStep.Clear: ClearIssued++; break;
                }
            }
            _nextTryTime = now + RetrySeconds;
            return TickResult.Continue;
        }
    }
}
