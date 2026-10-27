using System.Collections.Generic;
using UnityEngine;

namespace TrophyAPS.Protection
{
    internal struct InterceptRecord
    {
        public Vector3 position;
        public float time;
    }

    internal static class InterceptLog
    {
        private const int MAX_RECORDS = 16;

        private const float LIFETIME = 5f;
        private const float RADIUS = 40f;
        private const float RADIUS_SQR = RADIUS * RADIUS;

        private static readonly List<InterceptRecord> records = new List<InterceptRecord>(MAX_RECORDS);

        public static void Add(Vector3 position)
        {
            if (records.Count >= MAX_RECORDS) records.RemoveAt(0);

            records.Add(new InterceptRecord { position = position, time = Time.time });
        }

        public static bool AnyNear(Vector3 position)
        {
            float now = Time.time;

            for (int i = records.Count - 1; i >= 0; i--)
            {
                InterceptRecord r = records[i];

                if (now - r.time > LIFETIME) continue;
                if ((r.position - position).sqrMagnitude > RADIUS_SQR) continue;

                return true;
            }

            return false;
        }
    }
}
