using System;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;

namespace AutoMalfunction
{
    /// <summary>
    /// Player.FirearmController.Idling.Start runs every time a firearm goes back to idle: after a
    /// malfunction (StartMalfunction.SwitchToIdle), after the inspection
    /// (ExamineMalfunctionTypeOperation.SwitchToIdle), after drawing the weapon (SpawnOperation),
    /// and after reloads, checks and the like. The postfix is one bool and one enum check for
    /// healthy weapons, so the per-idle cost is negligible and nothing runs per frame.
    ///
    /// UtilityOperation (weapon inspection, check ammo) has its own Start(EUtilityType) and doesn't
    /// pass through here.
    /// </summary>
    [HarmonyPatch(typeof(Player.FirearmController.Idling), nameof(Player.FirearmController.Idling.Start), new[] { typeof(Action) })]
    internal static class IdlingStartPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Player.FirearmController.Idling __instance)
        {
            try
            {
                if (!Plugin.Enabled.Value)
                {
                    return;
                }
                Weapon weapon = __instance.Weapon;
                if (weapon == null || weapon.MalfState.State == Weapon.EMalfunctionState.None)
                {
                    return;
                }
                Player player = __instance.Player;
                if (player == null || !player.IsYourPlayer)
                {
                    return;
                }
                MalfunctionAutomator.Instance?.Arm(player, __instance.Controller, weapon);
            }
            catch (Exception e)
            {
                // Never let the mod break the game's own idle transition.
                Plugin.Log.LogError("Idling.Start postfix: " + e);
            }
        }
    }
}
