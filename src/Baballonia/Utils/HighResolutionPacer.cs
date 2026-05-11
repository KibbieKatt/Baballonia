using System;
using System.Diagnostics;
using System.Threading;

namespace Baballonia;

internal static class HighResolutionPacer
{
    public static void WaitUntil(long iterationStartTimestamp, TimeSpan cadence, CancellationToken cancellationToken)
    {
        if (cadence <= TimeSpan.Zero)
            return;

        var deadline = iterationStartTimestamp + (long)(cadence.TotalSeconds * Stopwatch.Frequency);
        while (!cancellationToken.IsCancellationRequested)
        {
            var remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
                return;

            var remainingMs = remainingTicks * 1000.0 / Stopwatch.Frequency;
            if (remainingMs > 2.0)
            {
                Thread.Sleep(1);
                continue;
            }

            if (remainingMs > 0.5)
            {
                Thread.Yield();
                continue;
            }

            Thread.SpinWait(64);
        }
    }
}
