using System;

namespace InfrastructureDecay
{
    // Pure raid-time scheduling keeps rolls independent of frame rate and lets
    // policy checks run without loading Unity or a player's profile.
    public sealed class OutageSchedule
    {
        private bool initialized;

        private double nextCheck;
        private double started;
        private double ends;

        public bool Active { get; private set; }
        public double Started => started;
        public double Ends => ends;

        public bool Tick(double now, double firstDelay, double repeatDelay, double duration, double chance, double roll)
        {
            if (!initialized)
            {
                initialized = true;
                nextCheck = now + Math.Max(0, firstDelay);
            }

            if (Active)
            {
                if (now < ends)
                {
                    return false;
                }
                Active = false;
                nextCheck = now + Math.Max(1, repeatDelay);
                return true;
            }

            if (now < nextCheck)
            {
                return false;
            }
            nextCheck = now + Math.Max(1, repeatDelay);
            if (roll >= Math.Max(0, Math.Min(1, chance)))
            {
                return false;
            }

            Start(now, duration);
            return true;
        }

        public void Start(double now, double duration)
        {
            initialized = true;
            Active = true;
            started = now;
            ends = now + Math.Max(1, duration);
        }

        public void Reset()
        {
            initialized = false;
            Active = false;
            nextCheck = 0;
            started = 0;
            ends = 0;
        }
    }
}
