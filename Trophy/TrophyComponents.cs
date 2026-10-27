using System.Collections.Generic;
using TrophyAPS.Protection;
using GHPC;
using GHPC.Utility;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace TrophyAPS.Protection
{
    internal static class TrophyConst
    {
        internal static readonly string[] QUADRANT_NAME = { "A", "B", "C", "D" };
    }

    internal class TrophyInstalledMarker : MonoBehaviour { }

    internal static class TrophyRegistry
    {
        public static readonly List<GHPC.Weapons.LiveRound> rounds = new List<GHPC.Weapons.LiveRound>();
        public static int vehicle_count;

        public static bool HasAny => vehicle_count > 0;

        private const int MAX_ROUNDS = 256;

        public static void Register(GHPC.Weapons.LiveRound round)
        {
            if (round == null) return;

            if (round.IsSpall) return;

            if (rounds.Contains(round)) return;

            if (rounds.Count >= MAX_ROUNDS) rounds.RemoveAt(0);

            rounds.Add(round);
        }

        public static void Prune()
        {
            for (int i = rounds.Count - 1; i >= 0; i--)
            {
                GHPC.Weapons.LiveRound r = rounds[i];

                if (r == null || r.gameObject == null || !r.gameObject.activeInHierarchy || r._detonated)
                {
                    rounds.RemoveAt(i);
                    continue;
                }

                if (r.CurrentSpeed < 5f && r.TimeTraveled > 2f)
                {
                    rounds.RemoveAt(i);
                }
            }
        }
    }

    [HarmonyPatch(typeof(GHPC.Weapons.LiveRound), "Start")]
    internal static class TrophyRoundTracker
    {
        private static void Postfix(GHPC.Weapons.LiveRound __instance)
        {
            if (!TrophyRegistry.HasAny) return;

            TrophyRegistry.Register(__instance);
        }
    }

    internal class TrophyLauncher : IAPSLauncher
    {
        private readonly Transform visual;
        private readonly float reload_seconds;
        private readonly string launcher_name;

        private int ammo;

        private readonly int ammo_max;

        private float last_fire_time = -999f;
        private float hidden_until = -1f;

        public TrophyLauncher(Transform ammo_visual, int ammo_count, float reload_seconds)
        {
            visual = ammo_visual;
            ammo = ammo_count;
            ammo_max = ammo_count;
            this.reload_seconds = reload_seconds;
            launcher_name = ammo_visual != null ? "Trophy/" + ammo_visual.name : "Trophy";
        }

        public Transform muzzle_fx;
        public Vector3 muzzle_euler = Vector3.zero;

        public float visual_restore_seconds = -1f;

        public bool IsEmpty => ammo <= 0;
        public int Remaining => ammo;
        public string Name => launcher_name;

        public void SetOwner(IUnit unit)
        {
            owner = unit;
        }

        private IUnit owner;

        public bool IsReloading(APSSchema schema)
        {
            return Time.time - last_fire_time < reload_seconds;
        }

        public void FireProjectile(Vector3 pos)
        {
            if (ammo <= 0 || visual == null) return;

            APSLauncher.PlayLaunch(pos, visual.position, owner);

            PlayMuzzleFx();

            SetVisible(false);

            float restore = visual_restore_seconds > 0f ? visual_restore_seconds : reload_seconds;

            hidden_until = Time.time + restore;
            ammo--;
            last_fire_time = Time.time;

            MelonLogger.Msg("[Trophy] " + launcher_name + " fired, " + ammo
                            + " rounds left (restored after reload)");
        }

        public void UpdateVisual()
        {
            if (hidden_until < 0f) return;
            if (Time.time < hidden_until) return;

            hidden_until = -1f;

            Refill();
        }

        private void Refill()
        {
            if (ammo > 0)
            {
                SetVisible(true);

                MelonLogger.Msg("[Trophy] " + launcher_name + " reload complete, "
                                + ammo + "/" + ammo_max + " rounds");

                return;
            }

            MelonLogger.Msg("[Trophy] " + launcher_name + " magazine empty (" + ammo_max
                            + " rounds spent), this side no longer engages");
        }

        public void RestoreNow()
        {
            if (hidden_until < 0f) return;

            hidden_until = -1f;

            Refill();
        }

        private void PlayMuzzleFx()
        {
            if (muzzle_fx == null)
            {
                ParticleSystem own = visual.GetComponentInChildren<ParticleSystem>();

                if (own != null) own.Play();

                return;
            }

            Transform launcher = visual.parent;

            if (launcher != null)
            {
                muzzle_fx.rotation = launcher.rotation * Quaternion.Euler(muzzle_euler);
            }

            ParticleSystem ps = muzzle_fx.GetComponent<ParticleSystem>();

            if (ps == null) ps = muzzle_fx.GetComponentInChildren<ParticleSystem>();

            if (ps != null)
            {
                ps.Clear();
                ps.Play();
            }
        }

        private void SetVisible(bool visible)
        {
            foreach (MeshRenderer renderer in visual.GetComponentsInChildren<MeshRenderer>(true))
            {
                renderer.enabled = visible;
            }
        }
    }

    internal class APSLauncherAnimator : MonoBehaviour
    {

        public float turn_speed = 720f;

        const float MODEL_BASE_YAW = 0f;

        public float idle_yaw = 90f;
        public float reload_yaw = 180f;

        public float yaw_min = 0f;
        public float yaw_max = 180f;

        public float fire_hold = 0.70f;

        public float reload_hold = 0.75f;

        public float idle_delay = 2f;

        const float PENDING_TIMEOUT = 0.3f;

        public IAPSLauncher launcher;
        public APSSchema schema;

        private Transform reference;

        private Quaternion rest_rotation;
        private float mount_yaw;
        private float current_yaw;
        private float target_yaw;
        private float pending_yaw;
        private float load_until = -1f;
        private bool ammo_restored = true;

        private bool empty_logged;
        private float fire_hold_until = -1f;
        private float last_aim_time = -999f;
        private bool has_pending;
        private bool was_reloading;

        private float ArcSpan => Mathf.Repeat(yaw_max - yaw_min, 360f);

        void Awake()
        {
            rest_rotation = transform.localRotation;

            current_yaw = idle_yaw;
            target_yaw = idle_yaw;
        }

        public void Configure(float min, float max)
        {
            yaw_min = min;
            yaw_max = max;

            idle_yaw = ClampToArc(idle_yaw);
            current_yaw = idle_yaw;
            target_yaw = idle_yaw;

            ApplyRotation();

            MelonLogger.Msg("[Trophy] " + name + " config: model base "
                            + mount_yaw.ToString("F1") + " deg, rest "
                            + idle_yaw.ToString("F1") + " deg, reload "
                            + reload_yaw.ToString("F1") + " deg, arc "
                            + yaw_min.ToString("F0") + "~" + yaw_max.ToString("F0") + "°");
        }

        public void SetReference(Transform ref_transform)
        {
            reference = ref_transform;

            Vector3 local = reference.InverseTransformPoint(transform.position);

            mount_yaw = MODEL_BASE_YAW;

            idle_yaw = local.x < 0f ? 270f : 90f;

            current_yaw = idle_yaw;
            target_yaw = idle_yaw;

            if (Mathf.Abs(local.x) < 0.05f)
            {
                MelonLogger.Warning("[Trophy] " + name + " X offset is near zero ("
                                    + local.x.ToString("F2") + "), cannot tell left from right,"
                                    + " assuming rest " + idle_yaw.ToString("F0") + " deg");
            }
        }

        private float CalcYaw(Vector3 world_direction)
        {
            if (reference != null) world_direction = reference.InverseTransformDirection(world_direction);

            world_direction.y = 0f;

            if (world_direction.sqrMagnitude < 1e-6f) return 0f;

            return Mathf.Repeat(
                Mathf.Atan2(world_direction.x, world_direction.z) * Mathf.Rad2Deg, 360f);
        }

        private float ClampToArc(float yaw)
        {
            float span = ArcSpan;

            if (span <= 0f || span >= 360f) return Mathf.Repeat(yaw, 360f);

            float rel = Mathf.Repeat(yaw - yaw_min, 360f);

            if (rel > span)
            {
                float over = rel - span;
                float under = 360f - rel;
                rel = over < under ? span : 0f;
            }

            return Mathf.Repeat(yaw_min + rel, 360f);
        }

        private bool InLoadPhase => load_until > 0f && Time.time < load_until;
        private bool InFireHold => fire_hold_until > 0f && Time.time < fire_hold_until;

        public void AimAt(Vector3 world_position)
        {
            if (launcher != null && launcher.IsEmpty) return;

            Vector3 origin = reference != null ? reference.position : transform.position;

            pending_yaw = ClampToArc(CalcYaw(world_position - origin));
            has_pending = true;
            last_aim_time = Time.time;

            if (InLoadPhase || InFireHold) return;

            target_yaw = pending_yaw;
        }

        void Update()
        {
            if (launcher != null)
            {
                launcher.UpdateVisual();

                bool reloading = schema != null && launcher.IsReloading(schema);

                if (reloading && !was_reloading)
                {
                    fire_hold_until = Time.time + fire_hold;

                    MelonLogger.Msg("[Trophy] " + name + " fired, holding "
                                    + fire_hold.ToString("F2") + " s (azimuth "
                                    + current_yaw.ToString("F0") + "°）");
                }

                was_reloading = reloading;

                if (reloading)
                {
                    if (InFireHold)
                    {
                    }
                    else if (launcher.IsEmpty)
                    {
                        target_yaw = idle_yaw;

                        if (!empty_logged)
                        {
                            empty_logged = true;

                            MelonLogger.Msg("[Trophy] " + name
                                            + " magazine empty, skipping reload, back to rest");
                        }
                    }
                    else if (load_until < 0f)
                    {
                        load_until = Time.time + reload_hold;
                        target_yaw = reload_yaw;
                        ammo_restored = false;

                        MelonLogger.Msg("[Trophy] " + name + " turning to reload position "
                                        + reload_yaw.ToString("F0") + "°");
                    }
                    else if (!InLoadPhase)
                    {
                        if (!ammo_restored)
                        {
                            ammo_restored = true;

                            TrophyLauncher trophy = launcher as TrophyLauncher;

                            if (trophy != null) trophy.RestoreNow();
                        }

                        target_yaw = PickNext();
                    }
                }
                else if (load_until > 0f)
                {
                    load_until = -1f;
                    fire_hold_until = -1f;
                    target_yaw = PickNext();
                }
            }

            if (load_until < 0f && !InFireHold && Time.time - last_aim_time > idle_delay)
            {
                target_yaw = idle_yaw;
            }

            StepToTarget();
            ApplyRotation();
        }

        private float PickNext()
        {
            return (has_pending && Time.time - last_aim_time <= PENDING_TIMEOUT)
                ? pending_yaw
                : idle_yaw;
        }

        private void StepToTarget()
        {
            float span = ArcSpan;

            float current_rel = Mathf.Repeat(current_yaw - yaw_min, 360f);
            float target_rel = Mathf.Repeat(target_yaw - yaw_min, 360f);

            if (span > 0f && span < 360f)
            {
                current_rel = Mathf.Clamp(current_rel, 0f, span);
                target_rel = Mathf.Clamp(target_rel, 0f, span);
            }

            current_rel = Mathf.MoveTowards(current_rel, target_rel, turn_speed * Time.deltaTime);

            current_yaw = Mathf.Repeat(yaw_min + current_rel, 360f);
        }

        private void ApplyRotation()
        {
            transform.localRotation =
                Quaternion.AngleAxis(current_yaw - mount_yaw, Vector3.up) * rest_rotation;
        }
    }

    internal class TrophyRadar : MonoBehaviour
    {
        public float warning_radius = 120f;
        public float threat_radius = 6f;

        private APSLauncherAnimator[] animators;

        private APSCollider[] quadrants;

        private float trigger_radius = 30f;
        private float trigger_height = 12f;

        private Unit radar_unit;

        private readonly Dictionary<int, Vector3> last_positions = new Dictionary<int, Vector3>();

        private const int MAX_TRACKED = 256;

        private float last_reject_log = -999f;

        private bool first_lock_logged;

        private float last_diag_time = -999f;

        private static readonly int[] QUADRANT_TO_LAUNCHER = { 1, 1, 0, 0 };

        private GHPC.Weapons.LiveRound[] threats;
        private float[] threat_sqr;

        private bool last_dual_tracking;

        public void Init(APSLauncherAnimator[] launcher_animators,
                         APSCollider[] quadrant_colliders,
                         float radius,
                         float height)
        {
            animators = launcher_animators;
            quadrants = quadrant_colliders;
            trigger_radius = radius;
            trigger_height = height;

            int launcher_count = animators == null ? 0 : animators.Length;

            threats = new GHPC.Weapons.LiveRound[launcher_count];
            threat_sqr = new float[launcher_count];

            if (quadrants != null && quadrants.Length > 0 && quadrants[0] != null)
            {
                radar_unit = quadrants[0].parent_unit as Unit;
            }

            MelonLogger.Msg("[Trophy] radar init: animators=" + (animators == null ? "null" : animators.Length.ToString())
                            + ", quadrants=" + (quadrants == null ? "null" : quadrants.Length.ToString())
                            + ", warning radius " + warning_radius.ToString("F0") + " m"
                            + ", detection volume " + trigger_radius.ToString("F0") + " x "
                            + trigger_height.ToString("F0") + " m"
                            + " (H x V, radar driven, no colliders)");
        }

        private string last_damage_status;

        private void CheckDamage()
        {
            if (radar_unit == null) return;

            string now = radar_unit.DamageStatus;

            if (now == last_damage_status) return;

            last_damage_status = now;

            MelonLogger.Msg("[Trophy] damage state changed: "
                            + (string.IsNullOrEmpty(now) ? "<none>" : now));
        }

        void Update()
        {
            if (animators == null || animators.Length == 0) return;
            if (threats == null || threat_sqr == null) return;

            CheckDamage();

            TrophyRegistry.Prune();

            if (last_positions.Count > MAX_TRACKED) last_positions.Clear();

            for (int a = 0; a < threats.Length; a++)
            {
                threats[a] = null;
                threat_sqr[a] = float.MaxValue;
            }

            if (TrophyRegistry.rounds.Count == 0)
            {
                last_dual_tracking = false;
                return;
            }

            Vector3 origin = transform.position;

            GHPC.Weapons.LiveRound nearest = null;
            float nearest_sqr = float.MaxValue;
            float nearest_azimuth = 0f;
            int nearest_quadrant = 0;

            int seen = 0, passed_speed = 0, passed_heading = 0;

            for (int i = TrophyRegistry.rounds.Count - 1; i >= 0; i--)
            {
                GHPC.Weapons.LiveRound round = TrophyRegistry.rounds[i];

                if (round == null) continue;

                seen++;

                float speed = round.CurrentSpeed;

                if (speed < 70f || speed > 700f) continue;

                passed_speed++;

                float caliber = 0f;

                if (round.ShotInfo != null && round.ShotInfo.TypeInfo != null)
                {
                    caliber = round.ShotInfo.TypeInfo.Caliber;
                }

                bool guided = round.Guided;

                if (!guided
                    && quadrants != null && quadrants.Length > 0 && quadrants[0] != null
                    && quadrants[0].schema != null
                    && caliber < quadrants[0].schema.min_engage_caliber) continue;

                Vector3 now = round.transform.position;
                int id = round.GetInstanceID();

                if ((now - origin).sqrMagnitude > warning_radius * warning_radius) continue;

                Vector3 prev;

                if (!last_positions.TryGetValue(id, out prev)) prev = now;

                last_positions[id] = now;

                Vector3 travel = now - prev;

                if (travel.sqrMagnitude > 2500f)
                {
                    prev = now;
                    travel = Vector3.zero;
                }

                Vector3 local = transform.InverseTransformPoint(round.transform.position);

                float azimuth = Mathf.Repeat(
                    Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg, 360f);

                int quadrant = Mathf.Clamp(Mathf.FloorToInt(azimuth / 90f), 0, 3);

                if (TryInterceptAt(round, prev, travel, quadrant, azimuth))
                {
                    continue;
                }

                bool heading_ok;

                if (guided)
                {
                    float prev_sqr = (prev - origin).sqrMagnitude;
                    float now_sqr = (now - origin).sqrMagnitude;

                    heading_ok = now_sqr <= prev_sqr
                              || InterceptionHandler.InsideEllipsoid(now, origin, trigger_radius, trigger_height);
                }
                else
                {
                    heading_ok = InterceptionHandler.IsHeadingForUs(round, origin, threat_radius, travel);
                }

                if (!heading_ok)
                {
                    if (Time.time - last_reject_log > 5f)
                    {
                        last_reject_log = Time.time;

                        MelonLogger.Msg("[Trophy] heading rejected (interception unaffected): " + round.name
                                        + "，travel=" + travel.ToString("F2")
                                        + "，forward=" + round.transform.forward.ToString("F2")
                                        + ", distance " + Vector3.Distance(now, origin).ToString("F1")
                                        + " m, caliber " + caliber.ToString("F0")
                                        + ", guided " + guided);
                    }

                    continue;
                }

                passed_heading++;

                float sqr = (round.transform.position - origin).sqrMagnitude;

                if (sqr < nearest_sqr)
                {
                    nearest_sqr = sqr;
                    nearest = round;
                    nearest_azimuth = azimuth;
                    nearest_quadrant = quadrant;
                }

                int launcher_index = QUADRANT_TO_LAUNCHER[quadrant];

                if (launcher_index >= threats.Length) continue;
                if (animators[launcher_index] == null) continue;

                IAPSLauncher owner = animators[launcher_index].launcher;

                if (owner != null && owner.IsEmpty) continue;

                if (sqr >= threat_sqr[launcher_index]) continue;

                threat_sqr[launcher_index] = sqr;
                threats[launcher_index] = round;
            }

            float interval = nearest != null ? 1f : 5f;

            if (Time.time - last_diag_time > interval)
            {
                last_diag_time = Time.time;

                MelonLogger.Msg("[Trophy] radar: in flight " + seen
                                + ", past speed window " + passed_speed
                                + ", heading at us " + passed_heading
                                + "，" + (nearest != null
                                    ? "tracking " + DescribeTracking()
                                    : "no threat"));
            }

            if (nearest == null) return;

            if (!first_lock_logged)
            {
                first_lock_logged = true;

                MelonLogger.Msg("[Trophy] first lock: " + nearest.name + ", azimuth "
                                + nearest_azimuth.ToString("F0") + " deg, quadrant "
                                + TrophyConst.QUADRANT_NAME[nearest_quadrant]
                                + " -> launcher " + QUADRANT_TO_LAUNCHER[nearest_quadrant]
                                + ", distance " + Mathf.Sqrt(nearest_sqr).ToString("F1") + " m");
            }

            int tracking = 0;

            for (int a = 0; a < threats.Length; a++)
            {
                if (threats[a] == null || animators[a] == null) continue;

                animators[a].AimAt(threats[a].transform.position);
                tracking++;
            }

            bool dual = tracking >= 2;

            if (dual != last_dual_tracking)
            {
                last_dual_tracking = dual;

                if (dual)
                {
                    MelonLogger.Msg("[Trophy] both launchers tracking: "
                                    + DescribeThreat(0) + " / " + DescribeThreat(1));
                }
            }

        }

        private string DescribeTracking()
        {
            string l = threats.Length > 0 && threats[0] != null
                ? Mathf.Sqrt(threat_sqr[0]).ToString("F0") + " m"
                : "—";

            string r = threats.Length > 1 && threats[1] != null
                ? Mathf.Sqrt(threat_sqr[1]).ToString("F0") + " m"
                : "—";

            return "L " + l + " / R " + r;
        }

        private string DescribeThreat(int index)
        {
            GHPC.Weapons.LiveRound r = threats[index];

            if (r == null) return "—";

            return "launcher " + index + " -> " + r.name
                   + "（" + Mathf.Sqrt(threat_sqr[index]).ToString("F0") + " m）";
        }

        private bool TryInterceptAt(GHPC.Weapons.LiveRound round, Vector3 prev_position, Vector3 travel,
                                    int quadrant, float azimuth)
        {
            if (quadrants == null || quadrants.Length == 0) return false;

            if (quadrant >= quadrants.Length) return false;

            bool handled = InterceptionHandler.TryInterceptOnApproach(round, quadrants[quadrant],
                                                                     trigger_radius, trigger_height,
                                                                     prev_position, travel);

            if (handled && !first_lock_logged)
            {
                first_lock_logged = true;

                MelonLogger.Msg("[Trophy] radar engaged (immediate intercept): " + round.name + ", azimuth "
                                + azimuth.ToString("F0") + " deg, quadrant " + TrophyConst.QUADRANT_NAME[quadrant]);
            }

            return handled;
        }
    }
}
