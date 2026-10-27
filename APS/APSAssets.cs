using System;
using GHPC.Vehicle;
using MelonLoader;
using UnityEngine;
using VanillaAssetUtil.AssetUtil;

namespace TrophyAPS.Protection
{
    internal static class APSAssets
    {
        internal static GameObject tow_front_blast;

        internal static void Load()
        {
            if (tow_front_blast != null) return;

            try
            {
                Vehicle m2 = AssetUtilApi.LoadVanillaVehicle("M2BRADLEY", temp: true);

                if (m2 == null)
                {
                    MelonLogger.Warning("[Trophy] Failed to load M2BRADLEY, launchers will have no muzzle flash");
                    return;
                }

                Transform fx = m2.transform.Find("Gun Scripts/Launcher M2 TOW/muzzle effects L/TOW Front FX");

                if (fx == null)
                {
                    MelonLogger.Warning("[Trophy] TOW Front FX node not found, launchers will have no muzzle flash");
                    return;
                }

                tow_front_blast = fx.gameObject;

                MelonLogger.Msg("[Trophy] Muzzle flash asset ready: " + tow_front_blast.name);
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[Trophy] Muzzle flash asset load failed: " + e.Message);
            }
        }
    }
}
