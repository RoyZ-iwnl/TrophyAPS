namespace TrophyAPS.Protection
{
    internal class APSSchema
    {
        public int min_engage_caliber = 70;
        public float min_engage_velocity = 70f;
        public float max_engage_velocity = 700f;
        public float intercept_delay = 2.0f;

        public bool enable_cooldown = false;

        public float[] hit_chance_by_speed = null;

        public float threat_radius = 6f;

        public static readonly float[] SPEED_BRACKETS = { 150f, 250f, 350f };

        public APSSchema() { }

        public APSSchema(int min_engage_caliber, float min_engage_velocity, float max_engage_velocity, float intercept_delay)
        {
            this.min_engage_caliber = min_engage_caliber;
            this.min_engage_velocity = min_engage_velocity;
            this.max_engage_velocity = max_engage_velocity;
            this.intercept_delay = intercept_delay;
        }

        public float GetHitChance(float speed)
        {
            if (hit_chance_by_speed == null || hit_chance_by_speed.Length == 0) return 1f;

            int index = BracketIndex(speed, SPEED_BRACKETS);

            if (index >= hit_chance_by_speed.Length) index = hit_chance_by_speed.Length - 1;

            return hit_chance_by_speed[index];
        }

        private static int BracketIndex(float value, float[] brackets)
        {
            for (int i = 0; i < brackets.Length; i++)
            {
                if (value < brackets[i]) return i;
            }

            return brackets.Length;
        }
    }
}
