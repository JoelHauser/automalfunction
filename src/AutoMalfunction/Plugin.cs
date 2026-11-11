using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace AutoMalfunction
{
    /// <summary>
    /// When the local player's weapon malfunctions, the character inspects it and clears it on their
    /// own, with the game's own operations, animations, sounds and timings. See
    /// <see cref="MalfunctionAutomator"/> for the sequence and <see cref="IdlingStartPatch"/> for the trigger.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.joelhauser.automalfunction";
        public const string PluginName = "Auto Malfunction";

        /// <summary>Must match the csproj's Version.</summary>
        public const string PluginVersion = "0.1.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<bool> DebugLogging;

        private Harmony _harmony;

        private void Awake()
        {
            Log = Logger;

            Enabled = Config.Bind(
                "1. General",
                "Clear malfunctions automatically",
                true,
                "When your weapon malfunctions, your character inspects it and clears it on their own, "
                + "at normal speed. Off: an inspection or repair already playing finishes, nothing new starts.");

            DebugLogging = Config.Bind(
                "2. Debug",
                "Debug logging",
                false,
                new ConfigDescription(
                    "Log each step to BepInEx/LogOutput.log. For bug reports.",
                    null,
                    new ConfigurationManagerAttributes { IsAdvanced = true }));

            var automator = gameObject.AddComponent<MalfunctionAutomator>();
            Enabled.SettingChanged += (_, __) =>
            {
                if (Enabled.Value)
                {
                    automator.ArmFromCurrentHands();
                }
                else
                {
                    automator.Cancel("mod disabled");
                }
            };

            _harmony = new Harmony(PluginGuid);
            _harmony.PatchAll(typeof(Plugin).Assembly);
            Log.LogInfo($"{PluginName} {PluginVersion} loaded");
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
        }

        internal static void Debug(string message)
        {
            if (DebugLogging != null && DebugLogging.Value)
            {
                Log.LogInfo(message);
            }
        }
    }

    /// <summary>Read by ConfigurationManager (F12) by field name; only the fields we set.</summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? IsAdvanced;
    }
}
