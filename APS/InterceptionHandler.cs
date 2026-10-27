using GHPC;
using HarmonyLib;
using MelonLoader;
using UnityEngine;

namespace TrophyAPS.Protection
{
    internal static class APSHooks
    {
        public static System.Action<GHPC.Weapons.LiveRound> Intercepted;
    }

    internal class APSMissMarker : MonoBehaviour
    {
        public IUnit owner_unit;
    }

    internal class APSLauncherHull : MonoBehaviour
    {
        public APSCollider[] linked_colliders;

        public bool Destroyed { get; private set; }

        public int LinkedCount => linked_colliders == null ? 0 : linked_colliders.Length;

        public void Link(APSCollider[] colliders)
        {
            linked_colliders = colliders;
        }

        public void DestroyHull()
        {
            if (Destroyed) return;
            Destroyed = true;

            int disabled = 0;

            if (linked_colliders != null)
            {
                foreach (APSCollider c in linked_colliders)
                {
                    if (c == null) continue;

                    c.Disable();
                    disabled++;
                }
            }

            MelonLogger.Msg("[Trophy] Launcher box destroyed: " + name
                                        + ", " + disabled + " detection pieces disabled");
        }
    }

    [HarmonyPatch(typeof(GHPC.Weapons.LiveRound), "penCheck")]
    internal static class InterceptionHandler
    {
        internal static bool friendly_filter_enabled = true;

        private static float last_friendly_log = -999f;

        private static bool Prefix(GHPC.Weapons.LiveRound __instance, object[] __args)
        {
            if (__instance.IsSpall)
            {
                if (InterceptLog.AnyNear(__instance.transform.position)) return false;

                return true;
            }

            Collider collider = (Collider)__args[0];
            Vector3 impact_point = (Vector3)__args[3];

            APSLauncherHull hull = collider.GetComponent<APSLauncherHull>();

            if (hull != null)
            {
                if (__instance.ShotInfo.TypeInfo.Caliber < 40f) return false;

                if (!hull.Destroyed)
                {
                    __instance.Story.builder.AppendLine("APS launcher destroyed");
                    hull.DestroyHull();
                }

                return true;
            }

            APSCollider aps = collider.GetComponent<APSCollider>();

            if (aps == null) return true;

            if (aps.radar_triggered) return true;

            if (aps.IsDisabled)
            {
                if (!aps.DisableLogged)
                {
                    aps.DisableLogged = true;

                    MelonLogger.Msg("[Trophy] Piece " + aps.name
                                                + " is disabled, rounds pass through (no further notices)");
                }

                return false;
            }

            TryIntercept(__instance, aps, impact_point,
                         __instance.transform.position - __instance._lastFramePosition);

            return false;
        }

        internal static bool TryInterceptOnApproach(GHPC.Weapons.LiveRound round,
                                                    APSCollider aps,
                                                    float radius,
                                                    float height,
                                                    Vector3 prev_position,
                                                    Vector3 travel)
        {
            if (round == null || aps == null) return false;
            if (aps.IsDisabled) return false;

            Vector3 center = aps.transform.position;
            Vector3 now = round.transform.position;

            Vector3 hit_point;

            if (!SegmentHitsEllipsoid(prev_position, now, center, radius, height, out hit_point)) return false;

            return TryIntercept(round, aps, hit_point, travel);
        }

        internal static bool SegmentHitsEllipsoid(Vector3 a, Vector3 b, Vector3 center,
                                                  float horizontal, float vertical,
                                                  out Vector3 point)
        {
            float k = horizontal / Mathf.Max(vertical, 0.01f);

            Vector3 ca = a - center;
            Vector3 cb = b - center;

            ca.y *= k;
            cb.y *= k;

            Vector3 hit;

            bool inside = SegmentHitsSphere(ca, cb, Vector3.zero, horizontal, out hit);

            hit.y /= k;
            point = center + hit;

            return inside;
        }

        internal static bool InsideEllipsoid(Vector3 point, Vector3 center,
                                             float horizontal, float vertical)
        {
            float k = horizontal / Mathf.Max(vertical, 0.01f);

            Vector3 d = point - center;
            d.y *= k;

            return d.sqrMagnitude <= horizontal * horizontal;
        }

        internal static bool SegmentHitsSphere(Vector3 a, Vector3 b, Vector3 center, float radius,
                                               out Vector3 point)
        {
            Vector3 delta = b - a;
            float len_sqr = delta.sqrMagnitude;

            if (len_sqr < 1e-8f)
            {
                point = a;
                return (a - center).sqrMagnitude <= radius * radius;
            }

            float t = Mathf.Clamp01(Vector3.Dot(center - a, delta) / len_sqr);
            point = a + delta * t;

            return (point - center).sqrMagnitude <= radius * radius;
        }

        internal static bool TryIntercept(GHPC.Weapons.LiveRound round, APSCollider aps,
                                          Vector3 impact_point, Vector3 travel)
        {
            if (round == null || aps == null) return false;
            if (round.IsSpall) return false;

            if (round.ShotInfo == null || round.ShotInfo.TypeInfo == null) return false;

            float caliber = round.ShotInfo.TypeInfo.Caliber;

            if (IsFriendlyFire(round, aps))
            {
                if (Time.time - last_friendly_log > 3f)
                {
                    last_friendly_log = Time.time;

                    Unit src = round.Shooter;

                    MelonLogger.Msg("[Trophy] Passed as friendly: " + round.name
                                    + ", from " + (src == null ? "?" : src.FriendlyName)
                                    + " (same vehicle: " + (ReferenceEquals(src, aps.parent_unit) ? "yes" : "no")
                                    + ", same platoon: " + SamePlatoon(src, aps.parent_unit) + ")");
                }

                return false;
            }

            if (!round.Guided && caliber < aps.schema.min_engage_caliber) return false;

            if (round.CurrentSpeed < aps.schema.min_engage_velocity
                || round.CurrentSpeed > aps.schema.max_engage_velocity) return false;

            bool use_hit_chance = aps.schema.hit_chance_by_speed != null;

            APSMissMarker marker = null;

            if (use_hit_chance)
            {
                marker = round.gameObject.GetComponent<APSMissMarker>();

                if (marker != null && ReferenceEquals(marker.owner_unit, aps.parent_unit)) return true;
            }

            if (use_hit_chance)
            {
                bool approaching_now;
                float miss = MissDistance(round.transform.position, travel, aps.transform.position,
                                          out approaching_now);

                MelonLogger.Msg("[Trophy] Engaging: closest approach "
                                            + miss.ToString("F1") + " m (threshold "
                                            + aps.schema.threat_radius.ToString("F1")
                                            + " m, approaching " + approaching_now + ", caliber "
                                            + caliber.ToString("F0") + " mm, speed "
                                            + round.CurrentSpeed.ToString("F0") + " m/s)");
            }

            round.Story.builder.AppendLine("Detected by APS radar");

            if (!aps.TryFireProjectile(impact_point))
            {
                round.Story.builder.AppendLine(aps.IsCoolingDown
                    ? "APS reloading"
                    : "APS launcher expended");

                if (use_hit_chance) Mark(round, aps.parent_unit, marker);

                return true;
            }

            if (use_hit_chance && UnityEngine.Random.value > aps.schema.GetHitChance(round.CurrentSpeed))
            {
                round.Story.builder.AppendLine("APS interceptor missed - threat inbound");

                MelonLogger.Msg("[Trophy] Missed: " + round.name
                                            + ", speed " + round.CurrentSpeed.ToString("F0") + " m/s"
                                            + ", hit chance "
                                            + (aps.schema.GetHitChance(round.CurrentSpeed) * 100f).ToString("F0")
                                            + "% (round flies on)");

                Mark(round, aps.parent_unit, marker);

                return true;
            }

            round.Story.builder.AppendLine("Intercepted by APS");

            MelonLogger.Msg("[Trophy] Intercepted: " + round.name
                                        + ", speed " + round.CurrentSpeed.ToString("F0") + " m/s");
            round._parentUnit = aps.parent_unit;
            round._frameData.VehicleStruck = aps.parent_unit;
            round.ShotInfo.Distance = Vector3.Distance(impact_point, round.Shooter.transform.position);
            round.reportShotTraceFrame();

            InterceptLog.Add(impact_point);

            round.Detonate();

            round.createExplosion(false, 0f, Vector3.zero, 0.03f, 0);

            if (APSHooks.Intercepted != null) APSHooks.Intercepted(round);

            return true;
        }

        private static void Mark(GHPC.Weapons.LiveRound round, IUnit owner, APSMissMarker marker)
        {
            if (marker == null) marker = round.gameObject.AddComponent<APSMissMarker>();

            marker.owner_unit = owner;
        }

        private static bool IsFriendlyFire(GHPC.Weapons.LiveRound round, APSCollider aps)
        {
            if (!friendly_filter_enabled) return false;

            IUnit self = aps.parent_unit;

            if (self == null) return false;

            Unit shooter = round.Shooter;

            if (shooter == null) return false;

            if (ReferenceEquals(shooter, self)) return true;

            if (round.ShotInfo != null && round.ShotInfo.IsPlayerShot) return false;

            if (SamePlatoon(shooter, self)) return true;

            string a = shooter.Allegiance.ToString();
            string b = self.Allegiance.ToString();

            if (a == b && a != "Neutral" && a != "None") return true;

            return false;
        }

        internal static bool SamePlatoon(Unit a, IUnit b)
        {
            if (a == null || b == null) return false;
            if (a.Platoon == null || b.Platoon == null) return false;

            return ReferenceEquals(a.Platoon, b.Platoon);
        }

        internal static bool IsHeadingForUs(GHPC.Weapons.LiveRound round, Vector3 center,
                                            float threat_radius, Vector3 travel)
        {
            bool approaching;

            return MissDistance(round.transform.position, travel, center, out approaching)
                   <= threat_radius && approaching;
        }

        internal static float MissDistance(Vector3 position, Vector3 travel, Vector3 center,
                                           out bool approaching)
        {
            Vector3 direction;

            if (travel.sqrMagnitude > 1e-4f)
            {
                direction = travel.normalized;
            }
            else
            {
                direction = center - position;

                if (direction.sqrMagnitude < 1e-4f)
                {
                    approaching = false;
                    return float.MaxValue;
                }

                direction = direction.normalized;
            }

            Vector3 to_unit = center - position;
            float along = Vector3.Dot(to_unit, direction);

            approaching = along > 0f;

            if (!approaching) return float.MaxValue;

            return (to_unit - direction * along).magnitude;
        }
    }
}
