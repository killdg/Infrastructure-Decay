using System;

namespace InfrastructureDecay
{
    public static class PowerFlickerPatterns
    {
        // Irregular contact interruptions, not interpolated brightness envelopes.
        // Each edge alternates full power/off; short bursts finish at either
        // sustained darkness or stable recovered power without a slow glow.
        private static readonly double[][] Outages =
        {
            new[] { 0d, .08, .15, .26, .31, 1d },
            new[] { 0d, .04, .12, .29, .36, .41, .68, 1d },
            new[] { 0d, .12, .38, .43, .49, 1d },
            new[] { 0d, .07, .11, .20, .37, .42, .51, 1d },
            new[] { 0d, .16, .22, .27, .62, 1d },
            new[] { 0d, .03, .18, .24, .33, .55, .60, 1d },
            new[] { 0d, .10, .14, .18, .43, .49, .77, 1d },
            new[] { 0d, .06, .32, .39, .48, .52, .58, 1d }
        };

        private static readonly double[][] Recovered =
        {
            new[] { 0d, .05, .11, .24, .30, .47, 1d },
            new[] { 0d, .09, .16, .21, .39, .48, .54, .62, 1d },
            new[] { 0d, .13, .31, .37, .44, .55, 1d },
            new[] { 0d, .04, .10, .18, .25, .39, .47, .69, 1d },
            new[] { 0d, .08, .12, .27, .36, .50, 1d },
            new[] { 0d, .06, .15, .32, .38, .43, .59, .64, 1d }
        };

        public const int OutageCount = 8;
        public const int RecoveredCount = 6;

        public static float Gain(int pattern, bool recovered, double progress)
        {
            if (progress <= 0)
            {
                return 1f;
            }
            if (progress >= 1)
            {
                return recovered ? 1f : 0f;
            }

            double[][] collection = recovered ? Recovered : Outages;
            double[] edges = collection[((pattern % collection.Length) + collection.Length) % collection.Length];
            int index = 0;
            while (index + 1 < edges.Length && progress >= edges[index + 1])
            {
                index++;
            }

            return index % 2 == 0 ? 1f : 0f;
        }
    }
}
