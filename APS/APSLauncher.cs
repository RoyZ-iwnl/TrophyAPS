using System;
using System.Linq;
using FMOD.Studio;
using GHPC;
using GHPC.Audio;
using GHPC.Effects;
using UnityEngine;

namespace TrophyAPS.Protection
{
    internal class APSLauncher : IAPSLauncher
    {
        private static AmmoType dummy_he;

        public IUnit parent_unit;
        private Transform[] projectiles;
        private int projectile_count = 0;
        private int current_projectile_idx = 0;
        public bool IsEmpty => projectile_count <= 0;

        private readonly string launcher_name = "APS";

        public int Remaining => projectile_count;

        public string Name => launcher_name;

        public void SetOwner(IUnit unit)
        {
            parent_unit = unit;
        }

        private float last_fire_time = -999f;

        private Transform hidden_projectile;
        private float hidden_until = 0f;

        public bool IsReloading(APSSchema schema)
        {
            if (schema == null || !schema.enable_cooldown || schema.intercept_delay <= 0f) return false;

            return Time.time - last_fire_time < schema.intercept_delay;
        }

        public static void Init()
        {
            if (dummy_he != null) return;

            dummy_he = new AmmoType();
            dummy_he.DetonateEffect = Resources.FindObjectsOfTypeAll<GameObject>().Where(o => o.name == "HE_explosion").First();
            dummy_he.ImpactEffectDescriptor = new ParticleEffectsManager.ImpactEffectDescriptor()
            {
                HasImpactEffect = true,
                ImpactCategory = ParticleEffectsManager.Category.HighExplosive,
                EffectSize = ParticleEffectsManager.EffectSize.MainGun,
                RicochetType = ParticleEffectsManager.RicochetType.None,
                Flags = ParticleEffectsManager.ImpactModifierFlags.Large,
                MinFilterStrictness = ParticleEffectsManager.FilterStrictness.Low
            };
        }

        public APSLauncher(Transform projectile_holder)
        {
            launcher_name = projectile_holder.name;
            projectile_count = projectile_holder.childCount;
            projectiles = new Transform[projectile_count];
            for (int i = 0; i < projectile_count; i++)
            {
                Transform projectile = projectile_holder.GetChild(i);
                projectiles[i] = projectile;

                if (APSAssets.tow_front_blast != null)
                {
                    GameObject fx = GameObject.Instantiate(APSAssets.tow_front_blast, projectile);
                    fx.transform.localPosition = Vector3.zero;
                    fx.transform.localEulerAngles = new Vector3(90f, 0f, 0f);
                }
            }
        }

        public void FireProjectile(Vector3 pos)
        {
            FireProjectile(pos, 0f);
        }

        public void FireProjectile(Vector3 pos, float hide_for_seconds)
        {
            if (projectile_count <= 0 || current_projectile_idx >= projectiles.Length)
            {
                MelonLoader.MelonLogger.Warning("[Trophy] " + launcher_name + " is empty, ignoring fire request");
                return;
            }

            Transform current_projectile = projectiles[current_projectile_idx];

            PlayLaunch(pos, current_projectile.position, parent_unit);

            current_projectile.GetComponentInChildren<ParticleSystem>().Play();
            current_projectile.gameObject.GetComponent<MeshRenderer>().enabled = false;
            projectile_count--;
            current_projectile_idx++;
            last_fire_time = Time.time;

            if (hide_for_seconds > 0f)
            {
                hidden_projectile = current_projectile;
                hidden_until = Time.time + hide_for_seconds;
            }

            MelonLoader.MelonLogger.Msg("[Trophy] " + launcher_name + " fired, " + projectile_count + " rounds left");
        }

        internal static void PlayLaunch(Vector3 impact_position, Vector3 muzzle_position, IUnit parent_unit)
        {
            try
            {
                Init();

                if (dummy_he != null && ParticleEffectsManager.Instance != null)
                {
                    ParticleEffectsManager.Instance.CreateImpactEffectOfType(dummy_he,
                        ParticleEffectsManager.FusedStatus.Fuzed,
                        ParticleEffectsManager.SurfaceMaterial.None, false, impact_position);
                }

                if (ImpactSFXManager.Instance != null)
                {
                    ImpactSFXManager.Instance.PlaySimpleImpactAudio(ImpactAudioType.Missile, impact_position);
                }

                FmodGenericAudioManager.PlayOneShot(
                    "event:/Weapons/launcher_9M14",
                    muzzle_position,
                    new ValueTuple<PARAMETER_ID, float>
                    (
                        FmodParameters.IsVehicleInterior,
                        VehicleParameterTracker.IsVehicleInterior(parent_unit) ? 1f : 0f
                    )
                );
            }
            catch (Exception e)
            {
                MelonLoader.MelonLogger.Warning("[Trophy] Launch effect/sound failed: " + e.Message);
            }
        }

        public void UpdateVisual()
        {
            if (hidden_projectile == null) return;

            if (Time.time < hidden_until) return;

            if (projectile_count > 0)
            {
                MeshRenderer renderer = hidden_projectile.gameObject.GetComponent<MeshRenderer>();

                if (renderer != null) renderer.enabled = true;
            }

            hidden_projectile = null;
        }
    }
}
