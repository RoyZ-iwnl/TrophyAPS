using System.Collections;
using GHPC.State;
using GHPC.Vehicle;
using MelonLoader;
using UnityEngine;
using TrophyAPS.Protection;

[assembly: MelonInfo(typeof(TrophyAPS.Mod), "TrophyAPS", "0.3.0", "simon_145.")]
[assembly: MelonGame("Radian Simulations LLC", "GHPC")]

namespace TrophyAPS
{
    public class Mod : MelonMod
    {
        internal static MelonPreferences_Category cfg;

        private static bool mounted;

        private static int probe_attempts;

        private float next_probe;

        public override void OnInitializeMelon()
        {
            cfg = MelonPreferences.CreateCategory("TrophyAPS");

            TrophyMount.Config(cfg);

            MelonLogger.Msg("[TrophyAPS] v0.3.0 standalone (Trophy only)");

            StateController.RunOrDefer(GameState.PlayerReady,
                new GameStateEventHandler(OnPlayerReady), GameStatePriority.Medium);

            MelonLogger.Msg("[TrophyAPS] PlayerReady callback registered");
        }

        public override void OnUpdate()
        {
            if (mounted) return;
            if (Time.time < next_probe) return;

            next_probe = Time.time + 1f;

            Vehicle[] vics = GameObject.FindObjectsByType<Vehicle>(FindObjectsSortMode.None);

            if (vics.Length == 0) return;

            if (probe_attempts++ >= 10)
            {
                MelonLogger.Error("[TrophyAPS] 10 polling attempts without mounting anything, giving up on this scene");
                mounted = true;
                return;
            }

            MelonLogger.Warning("[TrophyAPS] PlayerReady never fired, falling back to polling (attempt "
                                + probe_attempts + ")");

            Mount(vics);
        }

        public override void OnSceneWasInitialized(int buildIndex, string sceneName)
        {
            mounted = false;
            probe_attempts = 0;
            next_probe = 0f;
        }

        private static IEnumerator OnPlayerReady(GameState state)
        {
            if (mounted) yield break;

            MelonLogger.Msg("[TrophyAPS] PlayerReady fired");

            Mount(GameObject.FindObjectsByType<Vehicle>(FindObjectsSortMode.None));

            yield break;
        }

        private static void Mount(Vehicle[] vics)
        {
            APSAssets.Load();

            MelonLogger.Msg("[TrophyAPS] " + vics.Length + " vehicles in scene, matching");

            foreach (Vehicle vic in vics)
            {
                TrophyMount.HandleConversion(vic);
            }

            mounted = GameObject.FindObjectsByType<TrophyInstalledMarker>(FindObjectsSortMode.None).Length > 0;

            if (!mounted) MelonLogger.Warning("[TrophyAPS] No vehicle mounted this round, retrying in 1 s");
        }
    }
}
