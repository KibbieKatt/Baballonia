using System.Diagnostics;

namespace Baballonia.Services.Inference.Filters;

internal static class StopwatchCompat
{
    public static long GetTimestamp() => Stopwatch.GetTimestamp();

    public static float GetElapsedSeconds(ref long previousTimestamp)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedTicks = now - previousTimestamp;
        previousTimestamp = now;

        return elapsedTicks > 0
            ? (float)(elapsedTicks / (double)Stopwatch.Frequency)
            : 0.0f;
    }
}
