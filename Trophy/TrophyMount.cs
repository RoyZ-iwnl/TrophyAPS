using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TrophyAPS.Protection;
using GHPC;
using GHPC.Equipment;
using GHPC.Utility;
using GHPC.Vehicle;
using MelonLoader;
using MelonLoader.Utils;
using UnityEngine;

namespace TrophyAPS.Protection
{
    internal static class TrophyMount
    {
        static MelonPreferences_Entry<bool> trophy;
        static MelonPreferences_Entry<bool> friendly_filter;
        static MelonPreferences_Entry<float> trigger_radius;
        static MelonPreferences_Entry<float> trigger_height;
        static MelonPreferences_Entry<int> rounds_per_launcher;
        static MelonPreferences_Entry<bool> show_range;

        const float RADIUS_MIN = 5f;
        const float RADIUS_MAX = 30f;

        const float HEIGHT_MIN = 5f;
        const float HEIGHT_MAX = 30f;

        static readonly float[] QUADRANT_START_DEG = { 0f, 90f, 180f, 270f };

        const float QUADRANT_OVERLAP_DEG = 2f;

        const float HEMISPHERE_BASE_OFFSET = -1.5f;

        const int QUADRANT_AZ_SEGMENTS = 8;
        const int QUADRANT_EL_SEGMENTS = 6;

        const int ROUNDS_MIN = 1;
        const int ROUNDS_MAX = 8;

        const float COOLDOWN_SECONDS = 2.0f;

        const float AMMO_RESTORE_SECONDS = 2.0f;

        const string BUNDLE_NAME = "trophy";
        const string KIT_PREFAB = "trophy-kit";

        const float MODEL_SCALE = 1f;

        static readonly Vector3 MODEL_OFFSET = new Vector3(0f, 1.7f, 0.53f);

        const bool AUTO_YAW = false;
        const float MODEL_YAW = 90f;

        const bool AUTO_CENTER = false;

        private static AssetBundle trophy_bundle;
        private static GameObject trophy_prefab;
        private static bool trophy_load_failed;

        static readonly APSSchema schema = new APSSchema(40, 70f, 700f, 2f);

        static readonly float[] HIT_CHANCE_BY_SPEED = { 0.95f, 0.85f, 0.70f, 0.50f };

        static readonly string[] SPEED_LABEL = { "<150", "150-250", "250-350", ">=350" };

        static float active_radius = 30f;
        static float active_height = 12f;
        static int active_rounds = 4;

        public static void Config(MelonPreferences_Category cfg)
        {

            trophy = cfg.CreateEntry<bool>("Enabled", true);
            trophy.Comment = "Master switch. Disables mounting on every vehicle, useful for A/B comparison. "
                           + "APFSDS is never engaged; its velocity is far outside the window.";

            trigger_radius = cfg.CreateEntry<float>("Detection Radius", 30f);
            trigger_radius.Comment = "Horizontal semi-axis of the detection volume in meters - how far out a side-on threat "
                                   + "is engaged, 360 degrees all round. Range "
                                   + RADIUS_MIN + " ~ " + RADIUS_MAX + ", default 30. "
                                   + "Below 10 the interceptor blast spatters the vehicle and the AAR fills with Hit ... with blast";

            trigger_height = cfg.CreateEntry<float>("Detection Height", 12f);
            trigger_height.Comment = "Vertical semi-axis of the detection volume in meters - how high a target still counts "
                                   + "as incoming. Range "
                                   + HEIGHT_MIN + " ~ " + HEIGHT_MAX + ", default 12. "
                                   + "Smaller: rounds passing overhead are no longer engaged by mistake, but top-attack "
                                   + "rounds are caught later. "
                                   + "Larger: top-attack rounds are caught earlier, at the cost of engaging anything "
                                   + "passing a dozen meters overhead. "
                                   + "Equal to Radius degenerates into a sphere (old behaviour).";

            rounds_per_launcher = cfg.CreateEntry<int>("Rounds per Launcher", 4);
            rounds_per_launcher.Comment = "Rounds per launcher. Range "
                                        + ROUNDS_MIN + " ~ " + ROUNDS_MAX + ", default 4 (3 in the magazine plus 1 loaded). "
                                        + "The upper bound of 8 corresponds to 7+1.";

            friendly_filter = cfg.CreateEntry<bool>("Friendly Fire Filter", true);
            friendly_filter.Comment = "Let rounds fired by friendly units (same vehicle, same platoon, same allegiance) pass, "
                                    + "so two M1s close together do not intercept each other. "
                                    + "On the test range every vehicle is in the player switch list and counts as one side; "
                                    + "set this to false if the APS should engage vehicles you switched into.";

            show_range = cfg.CreateEntry<bool>("Show Range Visual", false);
            show_range.Comment = "Draw the detection volume outline of each quadrant (debug): A red, B yellow, C green, D blue. "
                               + "Turn it off once the shape has been verified.";

            schema.intercept_delay = COOLDOWN_SECONDS;
            schema.enable_cooldown = true;
            schema.hit_chance_by_speed = HIT_CHANCE_BY_SPEED;

            MelonLogger.Msg("[Trophy] reload cycle " + COOLDOWN_SECONDS.ToString("F2") + " s");

            string table = "[Trophy] hit chance: ";

            for (int i = 0; i < HIT_CHANCE_BY_SPEED.Length; i++)
            {
                table += SPEED_LABEL[i] + " → " + (HIT_CHANCE_BY_SPEED[i] * 100f).ToString("F0") + "%   ";
            }

            MelonLogger.Msg(table);
            MelonLogger.Msg("[Trophy] entry filter: caliber >= " + schema.min_engage_caliber + " mm, speed "
                            + schema.min_engage_velocity.ToString("F0") + " ~ "
                            + schema.max_engage_velocity.ToString("F0")
                            + " m/s (guided rounds ignore caliber, APFSDS stays outside the window)");

            try
            {
                MelonPreferences.Save();
            }
            catch (System.Exception e)
            {
                MelonLogger.Warning("[Trophy] failed to save config (harmless): " + e.Message);
            }

            APSHooks.Intercepted = round =>
            {
                if (round != null) TrophyRegistry.rounds.Remove(round);
            };

            MelonLogger.Msg("[Trophy] intercept callback registered: engaged rounds are dropped from the radar registry");
        }

        internal static void HandleConversion(Vehicle vic)
        {
            if (vic == null) return;
            if (!IsM1(vic)) return;

            MelonLogger.Msg("[Trophy] matched vehicle: " + vic.FriendlyName + " / " + vic.UniqueName);

            Transform mount = FindTurret(vic);

            if (mount == null)
            {
                MelonLogger.Error("[Trophy] turret mount point not found");
                return;
            }

            if (vic.GetComponent<TrophyInstalledMarker>() != null)
            {
                MelonLogger.Warning("[Trophy] " + vic.UniqueName + " already carries Trophy (marker present), skipping");
                return;
            }

            if (mount.Find("trophy_holder") != null)
            {
                MelonLogger.Warning("[Trophy] " + vic.UniqueName + " already has trophy_holder under the turret, skipping");
                return;
            }

            Transform holder = MakeScaledHolder(mount, "trophy_holder");

            APSLauncherAnimator[] animators;
            TrophyLauncher[] launchers = BuildLaunchers(holder, out animators);
            Transform[] colliders = BuildQuadrants(holder);

            int[][] assignments = new int[][]
            {
                new int[] { 1 },
                new int[] { 1 },
                new int[] { 0 },
                new int[] { 0 },
            };

            for (int q = 0; q < colliders.Length; q++)
            {
                APSCollider aps = colliders[q].gameObject.AddComponent<APSCollider>();
                aps.schema = schema;

                aps.radar_triggered = true;
                aps.Init(new IAPSLauncher[] { launchers[assignments[q][0]] });
            }

            APSCollider[] quadrants = new APSCollider[colliders.Length];

            for (int q = 0; q < colliders.Length; q++)
            {
                quadrants[q] = colliders[q].GetComponent<APSCollider>();
            }

            MelonLogger.Msg("[Trophy] routing: A, B -> trophy_launcher_1; C, D -> trophy_launcher_0; "
                            + active_rounds + " rounds each, " + (active_rounds * 2) + " total");

            BuildRangeVisual(holder);

            Transform box_l = FindDeep(holder, "APS-box-L");
            Transform box_r = FindDeep(holder, "APS-box-R");

            if (box_l != null) BindLauncherAarVisual(box_l.gameObject, 0);
            if (box_r != null) BindLauncherAarVisual(box_r.gameObject, 1);

            if (box_l == null || box_r == null)
            {
                MelonLogger.Warning("[Trophy] no APS-box-L/R in the model, launcher box AAR display skipped");
            }

            if (quadrants.Length == 4)
            {
                animators[0].launcher = FirstLauncher(quadrants[2]);
                animators[1].launcher = FirstLauncher(quadrants[0]);
                animators[0].schema = schema;
                animators[1].schema = schema;

            }

            InterceptionHandler.friendly_filter_enabled = friendly_filter.Value;

            TrophyRadar radar = holder.gameObject.AddComponent<TrophyRadar>();

            radar.threat_radius = schema.threat_radius;
            radar.Init(animators, quadrants, active_radius, active_height);

            TrophyRegistry.vehicle_count++;

            vic._friendlyName = vic.FriendlyName + " Trophy";

            vic.gameObject.AddComponent<TrophyInstalledMarker>();

            MelonLogger.Msg("[Trophy] mounted: " + vic.UniqueName + " 4 quadrant pieces + 2 launchers ("
                            + active_rounds + " rounds each, " + (active_rounds * 2) + " total) / detection volume "
                            + active_radius.ToString("F2") + " x " + active_height.ToString("F2") + " m");

            MelonLogger.Msg("[Trophy] scene: " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name
                            + ", friendly filter " + (friendly_filter.Value ? "on" : "off"));
        }

        private static Transform MakeScaledHolder(Transform parent, string name)
        {
            Vector3 s = parent.lossyScale;

            GameObject holder = new GameObject(name);
            holder.transform.SetParent(parent, false);
            holder.transform.localScale = new Vector3(
                Mathf.Approximately(s.x, 0f) ? 1f : 1f / s.x,
                Mathf.Approximately(s.y, 0f) ? 1f : 1f / s.y,
                Mathf.Approximately(s.z, 0f) ? 1f : 1f / s.z);

            MelonLogger.Msg("[Trophy] holder created, parent lossyScale=" + s.ToString("F2"));

            return holder.transform;
        }

        private static TrophyLauncher[] BuildLaunchers(Transform holder, out APSLauncherAnimator[] animators)
        {
            animators = new APSLauncherAnimator[2];

            int raw_rounds = rounds_per_launcher.Value;
            int rounds = Mathf.Clamp(raw_rounds, ROUNDS_MIN, ROUNDS_MAX);
            active_rounds = rounds;

            if (raw_rounds != rounds)
            {
                MelonLogger.Warning("[Trophy] Rounds per Launcher value " + raw_rounds
                                    + " outside " + ROUNDS_MIN + "~" + ROUNDS_MAX
                                    + ", clamped to " + rounds);
            }

            TrophyLauncher[] result = BuildLaunchersFromModel(holder, animators, rounds);

            if (result == null)
            {
                MelonLogger.Warning("[Trophy] model unavailable, falling back to procedural placeholder launchers");
                result = BuildLaunchersProcedural(holder, animators, rounds);
            }

            ApplyAnimatorSetup(animators, holder);

            return result;
        }

        private static void ApplyAnimatorSetup(APSLauncherAnimator[] animators, Transform holder)
        {
            for (int i = 0; i < animators.Length; i++)
            {
                if (animators[i] == null) continue;

                animators[i].SetReference(holder);

                if (i == 0)
                {
                    animators[i].Configure(180f, 360f);
                }
                else
                {
                    animators[i].Configure(0f, 180f);
                }
            }

            MelonLogger.Msg("[Trophy] posture: rest 90 / 270 deg (outward), reload 180 deg (rear)");
        }

        private static Transform AttachMuzzleFx(Transform ammo)
        {
            if (ammo == null) return null;

            if (ammo.GetComponentInChildren<ParticleSystem>() != null)
            {
                MelonLogger.Msg("[Trophy] " + ammo.name + " already has a particle system, using it");
                return null;
            }

            if (APSAssets.tow_front_blast == null)
            {
                MelonLogger.Warning("[Trophy] no muzzle flash asset available, launches will have no particles");
                return null;
            }

            GameObject fx = UnityEngine.Object.Instantiate(APSAssets.tow_front_blast, ammo);
            fx.name = "muzzle_fx";
            fx.transform.localPosition = Vector3.zero;
            fx.transform.localEulerAngles = Vector3.zero;
            fx.transform.localScale = Vector3.one;

            ParticleSystem ps = fx.GetComponentInChildren<ParticleSystem>();

            if (ps != null)
            {
                ParticleSystem.MainModule main = ps.main;
                main.simulationSpace = ParticleSystemSimulationSpace.World;

                main.loop = false;
                main.playOnAwake = false;
            }

            MelonLogger.Msg("[Trophy] attached muzzle flash to " + ammo.name
                            + (ps != null ? ", particle space World" : ", no ParticleSystem found"));

            return fx.transform;
        }

        private static TrophyLauncher[] BuildLaunchersFromModel(Transform holder,
                                                                APSLauncherAnimator[] animators,
                                                                int rounds)
        {
            GameObject kit = LoadTrophyKit(holder);

            if (kit == null) return null;

            Transform left = kit.transform.Find("APS-box-L/APS-launcher-L");
            Transform right = kit.transform.Find("APS-box-R/APS-launcher-R");

            if (left == null || right == null)
            {
                MelonLogger.Error("[Trophy] APS-box-L/APS-launcher-L (or the right-side counterpart) not found in the model");
                UnityEngine.Object.Destroy(kit);
                return null;
            }

            Transform ammo_left = left.Find("APS-ammo-L");
            Transform ammo_right = right.Find("APS-ammo-R");

            if (ammo_left == null || ammo_right == null)
            {
                MelonLogger.Error("[Trophy] APS-ammo-L or APS-ammo-R not found in the model");
                UnityEngine.Object.Destroy(kit);
                return null;
            }

            animators[0] = left.gameObject.AddComponent<APSLauncherAnimator>();
            animators[1] = right.gameObject.AddComponent<APSLauncherAnimator>();

            Transform fx_l = AttachMuzzleFx(ammo_left);
            Transform fx_r = AttachMuzzleFx(ammo_right);

            float left_x = holder.InverseTransformPoint(left.position).x;
            float right_x = holder.InverseTransformPoint(right.position).x;

            if (left_x > right_x)
            {
                MelonLogger.Warning("[Trophy] APS-launcher-L is on the +X side, left/right may be swapped: L="
                                    + left_x.ToString("F2") + "，R=" + right_x.ToString("F2"));
            }

            MelonLogger.Msg("[Trophy] model launchers ready: L(" + left_x.ToString("F2")
                            + ") R(" + right_x.ToString("F2") + "), " + rounds + " rounds each");

            TrophyLauncher launcher_l = new TrophyLauncher(ammo_left, rounds, COOLDOWN_SECONDS);
            launcher_l.muzzle_fx = fx_l;
            launcher_l.visual_restore_seconds = AMMO_RESTORE_SECONDS;

            TrophyLauncher launcher_r = new TrophyLauncher(ammo_right, rounds, COOLDOWN_SECONDS);
            launcher_r.muzzle_fx = fx_r;
            launcher_r.visual_restore_seconds = AMMO_RESTORE_SECONDS;

            return new TrophyLauncher[] { launcher_l, launcher_r };
        }

        private static GameObject LoadTrophyKit(Transform holder)
        {
            if (trophy_load_failed) return null;

            if (trophy_prefab == null)
            {
                string path = Path.Combine(MelonEnvironment.ModsDirectory + "/TrophyAPS", BUNDLE_NAME);

                if (trophy_bundle == null)
                {
                    trophy_bundle = AssetBundle.LoadFromFile(path);

                    if (trophy_bundle == null)
                    {
                        MelonLogger.Error("[Trophy] model bundle not found: " + path);
                        trophy_load_failed = true;
                        return null;
                    }
                }

                trophy_prefab = trophy_bundle.LoadAsset<GameObject>(KIT_PREFAB);

                if (trophy_prefab == null)
                {
                    GameObject[] all = trophy_bundle.LoadAllAssets<GameObject>();

                    if (all.Length > 0) trophy_prefab = all[0];
                }

                if (trophy_prefab == null)
                {
                    MelonLogger.Error("[Trophy] bundle contains no usable GameObject");
                    trophy_load_failed = true;
                    return null;
                }
            }

            GameObject instance = UnityEngine.Object.Instantiate(trophy_prefab, holder);
            instance.name = "trophy_model";

            Vector3 prefab_pos = instance.transform.localPosition;
            Vector3 prefab_euler = instance.transform.localEulerAngles;
            Vector3 prefab_scale = instance.transform.localScale;

            MelonLogger.Msg("[Trophy] prefab local: localPos " + prefab_pos.ToString("F3")
                            + "，euler " + prefab_euler.ToString("F1")
                            + "，localScale " + prefab_scale.ToString("F3"));

            instance.transform.localScale = prefab_scale * MODEL_SCALE;

            float yaw = AUTO_YAW ? ComputeYawFromRadars(instance, holder) : MODEL_YAW;

            instance.transform.localRotation =
                Quaternion.Euler(0f, yaw, 0f) * instance.transform.localRotation;

            Vector3 offset = prefab_pos + MODEL_OFFSET;

            bool prefab_has_position = prefab_pos.sqrMagnitude > 0.0001f;

            if (AUTO_CENTER && !prefab_has_position)
            {
                Bounds b = ComputeLocalBounds(instance, holder);

                if (b.size.sqrMagnitude > 0f)
                {
                    Vector3 auto_offset = new Vector3(-b.center.x, -b.min.y, -b.center.z);
                    offset += auto_offset;

                    MelonLogger.Msg("[Trophy] prefab has no position, centering on its base: geometric center "
                                    + b.center.ToString("F2")
                                    + ", base y=" + b.min.y.ToString("F2")
                                    + ", offset " + auto_offset.ToString("F2"));
                }
            }

            instance.transform.localPosition = offset;

            MelonLogger.Msg("[Trophy] model loaded: " + instance.name
                            + ", children " + instance.transform.childCount
                            + ", final offset " + offset.ToString("F2")
                            + ", yaw " + yaw.ToString("F1") + " deg");

            LogModelBounds(instance);

            return instance;
        }

        private static float ComputeYawFromRadars(GameObject kit, Transform holder)
        {
            Vector3? front = PairMidpoint(kit, holder, "radar-L1", "radar-R1");
            Vector3? rear = PairMidpoint(kit, holder, "radar-L2", "radar-R2");

            if (front == null || rear == null)
            {
                Transform single_front = FindDeep(kit.transform, "radar-L1");
                Transform single_rear = FindDeep(kit.transform, "radar-L2");

                if (single_front == null || single_rear == null)
                {
                    MelonLogger.Warning("[Trophy] radar nodes not found, cannot auto-align, "
                                        + "using MODEL_YAW=" + MODEL_YAW.ToString("F1") + " deg");
                    return MODEL_YAW;
                }

                MelonLogger.Warning("[Trophy] only one radar side available, heading estimate will be less accurate");

                front = holder.InverseTransformPoint(single_front.position);
                rear = holder.InverseTransformPoint(single_rear.position);
            }

            Vector3 dir = front.Value - rear.Value;
            dir.y = 0f;

            if (dir.sqrMagnitude < 0.0001f)
            {
                MelonLogger.Warning("[Trophy] front and rear radars overlap horizontally, cannot derive heading");
                return MODEL_YAW;
            }

            float angle = Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;

            MelonLogger.Msg("[Trophy] heading from radars: model nose off by " + angle.ToString("F1")
                            + " deg (relative to +Z), compensating " + (-angle).ToString("F1") + " deg");

            return -angle;
        }

        private static Vector3? PairMidpoint(GameObject kit, Transform holder,
                                             string left_key, string right_key)
        {
            Transform left = FindDeep(kit.transform, left_key);
            Transform right = FindDeep(kit.transform, right_key);

            if (left == null || right == null) return null;

            Vector3 a = holder.InverseTransformPoint(left.position);
            Vector3 b = holder.InverseTransformPoint(right.position);

            return (a + b) * 0.5f;
        }

        private static Transform FindDeep(Transform root, string key)
        {
            foreach (Transform child in root)
            {
                if (child.name.Contains(key)) return child;

                Transform found = FindDeep(child, key);

                if (found != null) return found;
            }

            return null;
        }

        private static Bounds ComputeLocalBounds(GameObject model, Transform reference)
        {
            Bounds empty = new Bounds(Vector3.zero, Vector3.zero);

            Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);

            if (renderers.Length == 0) return empty;

            Matrix4x4 to_local = reference != null
                ? reference.worldToLocalMatrix
                : Matrix4x4.identity;

            bool first = true;
            Bounds local = empty;

            foreach (Renderer r in renderers)
            {
                Bounds mb;
                Matrix4x4 to_world;

                SkinnedMeshRenderer smr = r as SkinnedMeshRenderer;

                if (smr != null && smr.sharedMesh != null)
                {
                    mb = smr.sharedMesh.bounds;
                    to_world = smr.transform.localToWorldMatrix;
                }
                else
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();

                    if (mf == null || mf.sharedMesh == null) continue;

                    mb = mf.sharedMesh.bounds;
                    to_world = mf.transform.localToWorldMatrix;
                }

                Vector3 c = mb.center;
                Vector3 e = mb.extents;

                for (int i = 0; i < 8; i++)
                {
                    Vector3 corner = c + new Vector3(
                        (i & 1) == 0 ? -e.x : e.x,
                        (i & 2) == 0 ? -e.y : e.y,
                        (i & 4) == 0 ? -e.z : e.z);

                    Vector3 p = to_local.MultiplyPoint3x4(to_world.MultiplyPoint3x4(corner));

                    if (first) { local = new Bounds(p, Vector3.zero); first = false; }
                    else local.Encapsulate(p);
                }
            }

            return first ? empty : local;
        }

        private static void LogModelBounds(GameObject model)
        {
            Transform reference = model.transform.parent;
            Bounds local = ComputeLocalBounds(model, reference);

            if (local.size.sqrMagnitude <= 0f)
            {
                MelonLogger.Warning("[Trophy] model has no Renderer, cannot measure size");
                return;
            }

            MelonLogger.Msg("[Trophy] model size (relative to mount) " + local.size.ToString("F2")
                            + " m, center " + local.center.ToString("F2"));

            int mesh_count = 0;

            foreach (Renderer r in model.GetComponentsInChildren<Renderer>(true))
            {
                if (r is MeshRenderer || r is SkinnedMeshRenderer) mesh_count++;
            }

            MelonLogger.Msg("[Trophy] meshes measured: " + mesh_count);

            Matrix4x4 to_local = reference != null
                ? reference.worldToLocalMatrix
                : Matrix4x4.identity;

            foreach (Transform child in model.transform)
            {
                Vector3 p = to_local.MultiplyPoint3x4(child.position);

                MelonLogger.Msg("[Trophy]   " + child.name + " at " + p.ToString("F2"));
            }
        }

        private static TrophyLauncher[] BuildLaunchersProcedural(Transform holder,
                                                                 APSLauncherAnimator[] animators,
                                                                 int rounds)
        {
            TrophyLauncher[] result = new TrophyLauncher[2];

            for (int i = 0; i < 2; i++)
            {
                GameObject launcher = new GameObject("trophy_launcher_" + i);
                launcher.transform.SetParent(holder, false);
                launcher.transform.localPosition =
                    new Vector3(i == 0 ? -1.2f : 1.2f, 0.3f, 0f);

                animators[i] = launcher.AddComponent<APSLauncherAnimator>();

                GameObject ammo = new GameObject("trophy_ammo_" + i);
                ammo.transform.SetParent(launcher.transform, false);
                ammo.AddComponent<MeshFilter>();
                ammo.AddComponent<MeshRenderer>();

                if (APSAssets.tow_front_blast != null)
                {
                    GameObject fx = UnityEngine.Object.Instantiate(APSAssets.tow_front_blast, ammo.transform);
                    fx.transform.localPosition = Vector3.zero;
                    fx.transform.localEulerAngles = Vector3.zero;
                }

                result[i] = new TrophyLauncher(ammo.transform, rounds, COOLDOWN_SECONDS);

                MelonLogger.Msg("[Trophy] " + launcher.name + " " + rounds + " rounds (single visual, auto reload)");
            }

            return result;
        }

        private static IAPSLauncher FirstLauncher(APSCollider collider)
        {
            if (collider == null || collider.Launchers == null || collider.Launchers.Length == 0) return null;

            return collider.Launchers[0];
        }

        private static Transform[] BuildQuadrants(Transform holder)
        {
            float raw_radius = trigger_radius.Value;
            float raw_height = trigger_height.Value;

            active_radius = Mathf.Clamp(raw_radius, RADIUS_MIN, RADIUS_MAX);
            active_height = Mathf.Clamp(raw_height, HEIGHT_MIN, HEIGHT_MAX);

            schema.threat_radius = active_radius * 0.5f;

            if (!Mathf.Approximately(raw_radius, active_radius))
            {
                MelonLogger.Warning("[Trophy] Detection Radius value " + raw_radius.ToString("F1")
                                    + " outside " + RADIUS_MIN + "~" + RADIUS_MAX
                                    + ", clamped to " + active_radius.ToString("F1"));
            }

            if (!Mathf.Approximately(raw_height, active_height))
            {
                MelonLogger.Warning("[Trophy] Detection Height value " + raw_height.ToString("F1")
                                    + " outside " + HEIGHT_MIN + "~" + HEIGHT_MAX
                                    + ", clamped to " + active_height.ToString("F1"));
            }

            Transform[] result = new Transform[4];

            for (int q = 0; q < 4; q++)
            {
                float az_start = QUADRANT_START_DEG[q] - QUADRANT_OVERLAP_DEG;
                float az_end = QUADRANT_START_DEG[q] + 90f + QUADRANT_OVERLAP_DEG;

                GameObject go = new GameObject("trophy_quadrant_" + TrophyConst.QUADRANT_NAME[q]);
                go.transform.SetParent(holder, false);
                go.transform.localPosition = new Vector3(0f, 0.3f, 0f);

                go.layer = 7;
                go.tag = "Untagged";

                Mesh mesh = BuildQuadrantMesh(active_radius, active_height, HEMISPHERE_BASE_OFFSET,
                                              az_start, az_end,
                                              QUADRANT_AZ_SEGMENTS, QUADRANT_EL_SEGMENTS);

                MeshCollider mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mesh;
                mc.convex = true;

                mc.isTrigger = false;
                mc.enabled = false;

                result[q] = go.transform;

                MelonLogger.Msg("[Trophy] quadrant " + TrophyConst.QUADRANT_NAME[q] + ": azimuth " + az_start.ToString("F0") + "~"
                                + az_end.ToString("F0") + " deg, vertices " + mesh.vertexCount
                                + ", mesh size " + mesh.bounds.size.ToString("F1"));
            }

            MelonLogger.Msg("[Trophy] detection volume: 4 convex pieces, ellipsoid " + active_radius.ToString("F2")
                            + " x " + active_height.ToString("F2")
                            + " m, base y=" + HEMISPHERE_BASE_OFFSET.ToString("F2")
                            + " m (relative to center) - colliders disabled, radar driven");

            DumpAncestors(result[0]);

            return result;
        }

        private static Mesh BuildQuadrantMesh(float radius, float height, float base_offset,
                                              float az_start_deg, float az_end_deg,
                                              int az_seg, int el_seg)
        {
            float th_min = Mathf.Asin(Mathf.Clamp(base_offset / Mathf.Max(height, 0.01f), -0.999f, 0.999f));
            float th_max = Mathf.PI * 0.5f;

            List<Vector3> verts = new List<Vector3>();
            List<int> tris = new List<int>();

            int stride = az_seg + 1;

            for (int j = 0; j < el_seg; j++)
            {
                float th = Mathf.Lerp(th_min, th_max, (float)j / el_seg);

                for (int i = 0; i <= az_seg; i++)
                {
                    float az = Mathf.Deg2Rad * Mathf.Lerp(az_start_deg, az_end_deg, (float)i / az_seg);
                    verts.Add(SurfacePoint(radius, height, th, az));
                }
            }

            for (int j = 0; j < el_seg - 1; j++)
            {
                for (int i = 0; i < az_seg; i++)
                {
                    int a = j * stride + i;
                    int b = a + 1;
                    int c = a + stride;
                    int d = c + 1;

                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            }

            int pole = verts.Count;
            verts.Add(new Vector3(0f, height, 0f));

            int last_row = (el_seg - 1) * stride;

            for (int i = 0; i < az_seg; i++)
            {
                tris.Add(last_row + i); tris.Add(pole); tris.Add(last_row + i + 1);
            }

            int center = verts.Count;
            verts.Add(new Vector3(0f, base_offset, 0f));

            for (int i = 0; i < az_seg; i++)
            {
                tris.Add(center); tris.Add(i); tris.Add(i + 1);
            }

            for (int j = 0; j < el_seg - 1; j++)
            {
                int l0 = j * stride;
                int l1 = l0 + stride;
                tris.Add(center); tris.Add(l1); tris.Add(l0);

                int r0 = j * stride + az_seg;
                int r1 = r0 + stride;
                tris.Add(center); tris.Add(r0); tris.Add(r1);
            }

            Mesh mesh = new Mesh();
            mesh.name = "trophy_quadrant";
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            return mesh;
        }

        private static Vector3 SurfacePoint(float radius, float height, float theta, float azimuth)
        {
            float ring = radius * Mathf.Cos(theta);

            return new Vector3(ring * Mathf.Sin(azimuth),
                               height * Mathf.Sin(theta),
                               ring * Mathf.Cos(azimuth));
        }

        private static void BuildRangeVisual(Transform holder)
        {
            if (!show_range.Value) return;

            Shader shader = null;
            string[] candidates =
            {
                "Sprites/Default",
                "Unlit/Color",
                "Legacy Shaders/Particles/Alpha Blended",
                "Standard"
            };

            foreach (string name in candidates)
            {
                shader = Shader.Find(name);

                if (shader != null)
                {
                    MelonLogger.Msg("[Trophy] range visual using shader: " + name);
                    break;
                }
            }

            if (shader == null)
            {
                MelonLogger.Warning("[Trophy] no usable LineRenderer shader, visual skipped");
                return;
            }

            GameObject root = new GameObject("trophy_range_visual");
            root.transform.SetParent(holder, false);
            root.transform.localPosition = new Vector3(0f, 0.3f, 0f);
            root.layer = 0;

            Material mat = new Material(shader);

            Color[] colors =
            {
                new Color(1f, 0.25f, 0.25f, 0.90f),
                new Color(1f, 0.85f, 0.20f, 0.90f),
                new Color(0.30f, 0.90f, 0.35f, 0.90f),
                new Color(0.35f, 0.55f, 1f, 0.90f),
            };

            string[] color_names = { "red", "yellow", "green", "blue" };

            float th_min_deg = Mathf.Asin(Mathf.Clamp(HEMISPHERE_BASE_OFFSET / Mathf.Max(active_height, 0.01f),
                                                      -0.999f, 0.999f))
                               * Mathf.Rad2Deg;

            for (int q = 0; q < 4; q++)
            {
                float az0 = QUADRANT_START_DEG[q];
                float az1 = az0 + 90f;
                string tag = "quadrant_" + TrophyConst.QUADRANT_NAME[q];

                AddArc(root.transform, mat, colors[q], tag + "_base", az0, az1, th_min_deg, 0.18f);
                AddArc(root.transform, mat, colors[q], tag + "_mid", az0, az1, 45f, 0.12f);
                AddMeridian(root.transform, mat, colors[q], tag + "_edge0", az0, th_min_deg, 0.10f);
                AddMeridian(root.transform, mat, colors[q], tag + "_edge1", az1, th_min_deg, 0.10f);

                MelonLogger.Msg("[Trophy] visual quadrant " + TrophyConst.QUADRANT_NAME[q] + " = " + color_names[q]
                                + ", azimuth " + az0.ToString("F0") + "~" + az1.ToString("F0") + " deg");
            }

            MelonLogger.Msg("[Trophy] range visual: ellipsoid " + active_radius.ToString("F2")
                            + " x " + active_height.ToString("F2")
                            + " m, base y=" + HEMISPHERE_BASE_OFFSET.ToString("F2") + " m (relative to center)");
        }

        private static void AddArc(Transform parent, Material mat, Color color, string name,
                                   float az0, float az1, float theta_deg, float width)
        {
            const int seg = 16;

            LineRenderer lr = NewLine(parent, mat, color, name, width);
            lr.loop = false;
            lr.positionCount = seg + 1;

            float th = Mathf.Deg2Rad * theta_deg;

            for (int i = 0; i <= seg; i++)
            {
                float az = Mathf.Deg2Rad * Mathf.Lerp(az0, az1, (float)i / seg);
                lr.SetPosition(i, SurfacePoint(active_radius, active_height, th, az));
            }
        }

        private static void AddMeridian(Transform parent, Material mat, Color color, string name,
                                        float az_deg, float theta_min_deg, float width)
        {
            const int seg = 12;

            LineRenderer lr = NewLine(parent, mat, color, name, width);
            lr.loop = false;
            lr.positionCount = seg + 1;

            float az = Mathf.Deg2Rad * az_deg;

            for (int i = 0; i <= seg; i++)
            {
                float th = Mathf.Deg2Rad * Mathf.Lerp(theta_min_deg, 90f, (float)i / seg);
                lr.SetPosition(i, SurfacePoint(active_radius, active_height, th, az));
            }
        }

        private static LineRenderer NewLine(Transform parent, Material mat, Color color, string name, float width)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = Vector3.zero;
            go.layer = 0;

            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.startWidth = width;
            lr.endWidth = width;
            lr.numCapVertices = 0;
            lr.numCornerVertices = 0;
            lr.material = mat;
            lr.startColor = color;
            lr.endColor = color;
            lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            lr.receiveShadows = false;

            return lr;
        }

        public static void BindLauncherAarVisual(GameObject hull, int launcher_index)
        {
            if (hull == null) return;

            MeshFilter filter = hull.GetComponent<MeshFilter>();

            if (filter == null || filter.sharedMesh == null)
            {
                MelonLogger.Warning("[Trophy] launcher box " + hull.name + " has no MeshFilter, AAR display skipped");
                return;
            }

            GameObject proxy = new GameObject(hull.name + "_aar");
            proxy.transform.SetParent(hull.transform, false);
            proxy.layer = 8;

            MeshFilter proxy_filter = proxy.AddComponent<MeshFilter>();
            proxy_filter.sharedMesh = filter.sharedMesh;

            MeshRenderer proxy_renderer = proxy.AddComponent<MeshRenderer>();

            MeshRenderer source = hull.GetComponent<MeshRenderer>();

            if (source == null) source = hull.GetComponentInChildren<MeshRenderer>();

            if (source != null) proxy_renderer.sharedMaterials = source.sharedMaterials;

            UniformArmor armour = proxy.AddComponent<UniformArmor>();
            armour.SetName("Trophy launcher");
            armour.PrimaryHeatRha = 15f;
            armour.PrimarySabotRha = 15f;
            armour.SecondaryHeatRha = 0f;
            armour.SecondarySabotRha = 0f;
            armour._noImpactDecals = true;

            AarVisual aar = proxy.AddComponent<AarVisual>();
            aar.SwitchMaterials = true;
            aar.HideUntilAar = true;

            MelonLogger.Msg("[Trophy] launcher box " + hull.name + " wired into AAR display (no collision, indestructible)");
        }

        private static Transform FindTurret(Vehicle vic)
        {
            AimablePlatform[] platforms = vic.AimablePlatforms;

            if (platforms != null && platforms.Length > 1 && platforms[1] != null)
            {
                LateFollowTarget t = platforms[1].transform.GetComponentInParent<LateFollowTarget>();

                if (t != null && t._lateFollowers.Count > 0) return t._lateFollowers[0].transform;

                return platforms[1].transform;
            }

            LateFollowTarget fallback = vic.GetComponent<LateFollowTarget>();

            if (fallback != null && fallback._lateFollowers.Count > 0)
                return fallback._lateFollowers[0].transform;

            return null;
        }

        private static void DumpAncestors(Transform t)
        {
            string chain = "";

            while (t != null)
            {
                bool has_lf = t.GetComponent<LateFollow>() != null;
                chain += t.name + (has_lf ? "[LateFollow]" : "") + " < ";

                if (has_lf) break;
                t = t.parent;
            }

            MelonLogger.Msg("[Trophy] detection volume ancestry: " + chain + "END");

            LateFollow lf = null;
            Transform cursor = t;

            while (cursor != null && lf == null)
            {
                lf = cursor.GetComponent<LateFollow>();
                cursor = cursor.parent;
            }

            if (lf == null)
            {
                MelonLogger.Error("[Trophy] LateFollow not found - APSCollider cannot resolve parent_unit, interception will not work");
            }
            else
            {
                UnityEngine.Object owner = lf.ParentUnit as UnityEngine.Object;
                MelonLogger.Msg("[Trophy] LateFollow found, ParentUnit="
                                + (owner != null ? owner.name : "null"));
            }
        }

        private static bool IsM1(Vehicle vic)
        {
            string friendly = vic.FriendlyName;
            string unique = vic.UniqueName;

            if (!string.IsNullOrEmpty(unique))
            {
                if (unique == "M1" || unique == "M1IP") return true;
                if (unique.StartsWith("M1IP") || unique.StartsWith("M1 ")) return true;
                if (unique.StartsWith("M1_")) return true;
            }

            if (!string.IsNullOrEmpty(friendly))
            {
                if (friendly == "M1"
                    || friendly == "M1IP"
                    || friendly == "M1A1"
                    || friendly.StartsWith("M1 ")) return true;
            }

            return false;
        }

    }
}
