using System.Linq;
using GHPC;
using GHPC.Utility;
using UnityEngine;

namespace TrophyAPS.Protection
{
    internal interface IAPSLauncher
    {
        bool IsEmpty { get; }
        int Remaining { get; }
        string Name { get; }

        void SetOwner(IUnit unit);

        bool IsReloading(APSSchema schema);

        void FireProjectile(Vector3 pos);

        void UpdateVisual();
    }

    internal class APSCollider : MonoBehaviour
    {
        public IUnit parent_unit;
        private IAPSLauncher[] assigned_launchers;
        public APSSchema schema;

        public bool radar_triggered;

        public IAPSLauncher[] Launchers => assigned_launchers;

        public bool IsCoolingDown { get; private set; }

        public bool IsDisabled { get; private set; }

        internal bool DisableLogged;

        public void Disable()
        {
            IsDisabled = true;
        }

        void Awake()
        {
            parent_unit = this.GetComponentInParent<LateFollow>().ParentUnit;
        }

        public void Init(IAPSLauncher[] launchers)
        {
            assigned_launchers = launchers;

            foreach (IAPSLauncher launcher in assigned_launchers)
            {
                if (launcher != null) launcher.SetOwner(parent_unit);
            }
        }

        public bool TryFireProjectile(Vector3 pos)
        {
            IsCoolingDown = false;

            if (IsDisabled) return false;

            if (assigned_launchers == null || assigned_launchers.Length == 0) return false;

            if (schema == null || !schema.enable_cooldown)
            {
                IAPSLauncher launcher = assigned_launchers.Where(o => !o.IsEmpty).FirstOrDefault();

                if (launcher == null) return false;

                launcher.FireProjectile(pos);
                return true;
            }

            foreach (IAPSLauncher launcher in assigned_launchers)
            {
                if (launcher == null || launcher.IsEmpty) continue;

                if (launcher.IsReloading(schema))
                {
                    IsCoolingDown = true;
                    continue;
                }

                launcher.FireProjectile(pos);

                MelonLoader.MelonLogger.Msg("[Trophy] " + gameObject.name + " -> " + launcher.Name
                                            + ", " + launcher.Remaining + " rounds left");

                return true;
            }

            return false;
        }
    }
}
