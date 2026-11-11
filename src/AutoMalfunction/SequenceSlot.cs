namespace AutoMalfunction
{
    /// <summary>
    /// Which malfunction is being worked on, and which one we gave up on. Game-free (weapons and
    /// controllers are compared by reference) so tools/Harness runs the same arming rules as the game.
    /// </summary>
    internal sealed class SequenceSlot
    {
        private object _controller;
        private object _weapon;

        // A malfunction we stopped on is identified by its weapon and LastMalfunctionTime, which the
        // fire operations set when a new malfunction happens. Going idle again on it doesn't restart.
        private object _gaveUpWeapon;
        private float _gaveUpMalfTime;

        internal MalfunctionSequence Current { get; private set; }

        internal bool Pending => Current != null;

        /// <summary>
        /// The weapon went idle with a malfunction. Returns false if this is a malfunction we gave
        /// up on. Starts a new sequence for a different weapon or controller (the caller has to
        /// cancel the old one first; see <see cref="IsFor"/>), or wakes the current one.
        /// </summary>
        internal bool Arm(object controller, object weapon, float lastMalfunctionTime, float now, int frame, out bool started)
        {
            started = false;
            if (ReferenceEquals(_gaveUpWeapon, weapon) && _gaveUpMalfTime == lastMalfunctionTime)
            {
                return false;
            }
            if (!IsFor(controller, weapon))
            {
                _controller = controller;
                _weapon = weapon;
                Current = new MalfunctionSequence();
                started = true;
            }
            Current.Wake(now, frame);
            return true;
        }

        internal bool IsFor(object controller, object weapon) =>
            Pending && ReferenceEquals(_controller, controller) && ReferenceEquals(_weapon, weapon);

        /// <summary>Ends the current sequence. A give-up is remembered against the malfunction.</summary>
        internal void End(TickResult result, float lastMalfunctionTime)
        {
            if (result == TickResult.GaveUp && _weapon != null)
            {
                _gaveUpWeapon = _weapon;
                _gaveUpMalfTime = lastMalfunctionTime;
            }
            _controller = null;
            _weapon = null;
            Current = null;
        }
    }
}
