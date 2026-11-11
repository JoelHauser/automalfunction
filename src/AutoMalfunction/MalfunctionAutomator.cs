using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace AutoMalfunction
{
    /// <summary>
    /// Runs the <see cref="MalfunctionSequence"/> for the local player's weapon, and presses
    /// through the same FirearmController calls the "inspect weapon" and "check chamber / fix
    /// malfunction" keys make.
    ///
    /// The component is disabled (no Update) whenever nothing is pending. <see cref="IdlingStartPatch"/>
    /// arms it when the local player's firearm goes back to idle with a malfunction. That is the
    /// moment vanilla first lets the player act: after StartMalfunction, after the inspection,
    /// after drawing a malfunctioned weapon, and after anything that interrupted us.
    /// </summary>
    internal sealed class MalfunctionAutomator : MonoBehaviour, IMalfunctionHost
    {
        internal static MalfunctionAutomator Instance;

        private readonly SequenceSlot _slot = new SequenceSlot();
        private Player _player;
        private Player.FirearmController _controller;
        private Weapon _weapon;

        private bool Pending => _slot.Pending;

        private void Awake()
        {
            Instance = this;
            enabled = false;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

        /// <summary>
        /// Called when the local player's firearm enters idle with a malfunction. Starts a sequence,
        /// or wakes the one already running for the same weapon without resetting its counters.
        /// </summary>
        internal void Arm(Player player, Player.FirearmController controller, Weapon weapon)
        {
            if (Pending && !_slot.IsFor(controller, weapon))
            {
                Cancel("a different weapon went idle");
            }
            if (!_slot.Arm(controller, weapon, weapon.MalfState.LastMalfunctionTime, Time.time, Time.frameCount, out bool started))
            {
                return;
            }
            if (started)
            {
                _player = player;
                _controller = controller;
                _weapon = weapon;
                Plugin.Debug($"Armed: {weapon.ShortName.Localized()} {weapon.MalfState.State}");
            }
            enabled = true;
        }

        /// <summary>
        /// Drops the pending sequence. An inspection or repair the game has already started is a
        /// vanilla operation and plays out normally; this only stops further presses.
        /// </summary>
        internal void Cancel(string reason)
        {
            if (Pending)
            {
                Plugin.Debug("Stopped: " + reason);
            }
            _slot.End(TickResult.Cancelled, 0f);
            _player = null;
            _controller = null;
            _weapon = null;
            enabled = false;
        }

        /// <summary>When the mod is switched on mid-raid, pick up a malfunction already in hand.</summary>
        internal void ArmFromCurrentHands()
        {
            Player player = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance.MainPlayer : null;
            if (player == null || !player.IsYourPlayer)
            {
                return;
            }
            if (player.HandsController is Player.FirearmController controller
                && controller != null
                && controller.Item != null
                && controller.Item.MalfState.State != Weapon.EMalfunctionState.None)
            {
                Arm(player, controller, controller.Item);
            }
        }

        private void Update()
        {
            if (!Pending)
            {
                enabled = false;
                return;
            }

            MalfunctionSequence sequence = _slot.Current;
            TickResult result = sequence.Tick(this, Time.time, Time.frameCount);
            if (result == TickResult.Continue)
            {
                return;
            }
            if (result == TickResult.GaveUp)
            {
                Plugin.Log.LogWarning(
                    $"Stopped on {_weapon.MalfState.State} on {_weapon.ShortName.Localized()}: "
                    + sequence.LastReason + ". Clear it manually.");
            }
            Plugin.Debug("Stopped: " + sequence.LastReason);
            _slot.End(result, _weapon.MalfState.LastMalfunctionTime);
            _player = null;
            _controller = null;
            _weapon = null;
            enabled = false;
        }

        public string StaleReason()
        {
            if (!Plugin.Enabled.Value)
            {
                return "mod disabled";
            }
            // Unity null: the player or the hands controller was destroyed (raid exit, weapon put away).
            if (_player == null || _controller == null)
            {
                return "player or weapon controller gone";
            }
            if (_player.HealthController == null || !_player.HealthController.IsAlive)
            {
                return "player dead";
            }
            if (!ReferenceEquals(_player.HandsController, _controller))
            {
                return "hands changed";
            }
            if (_controller.Item != _weapon)
            {
                return "weapon in hands changed";
            }
            return null;
        }

        public bool Malfunctioned => _weapon.MalfState.State != Weapon.EMalfunctionState.None;
        public bool KnowsMalfunction => _weapon.MalfState.IsKnownMalfunction(_player.ProfileId);
        public bool KnowsType => _weapon.MalfState.IsKnownMalfType(_player.ProfileId);

        /// <summary>
        /// The conditions under which a key press would be taken, checked before pressing so a
        /// refused press has no side effects. FirearmController.CheckChamber/ExamineWeapon call
        /// RemoveLeftHandItem and leave mounted state before they check anything else, and the
        /// UtilityOperation (an Idling subclass) answers ExamineWeapon with true without doing
        /// anything, so "is Idling" is not enough: it has to be exactly Idling.
        /// </summary>
        public bool GameAllowsAction()
        {
            if (_controller.CurrentOperation == null
                || _controller.CurrentOperation.GetType() != typeof(Player.FirearmController.Idling))
            {
                return false;
            }
            if (_controller.Blindfire || _controller.IsTriggerPressed)
            {
                return false;
            }
            if (_player.IsInventoryOpened)
            {
                return false;
            }
            // Vanilla would pull the player off a mount; we wait for them to leave it instead.
            if (_player.MovementContext.IsInMountedState || _controller.IsStationaryWeapon)
            {
                return false;
            }
            if (_player.LeftHandController != null && _player.LeftHandController.InAction)
            {
                return false;
            }
            if (_controller.Item is RocketLauncher)
            {
                return false;
            }
            return !_player.InventoryController.HasAnyHandsAction();
        }

        public bool Press(MalfStep step)
        {
            bool taken = step == MalfStep.Inspect ? _controller.ExamineWeapon() : _controller.CheckChamber();
            Plugin.Debug($"{step} on {_weapon.MalfState.State} -> {(taken ? "taken" : "refused")}");
            return taken;
        }
    }
}
