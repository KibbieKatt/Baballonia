using System;
namespace Baballonia.Services.Inference.Filters;

public sealed class StateAdaptiveFirFilter : IFilter
{
    private static readonly float[] Weights5 = [1f, 2f, 3f, 2f, 1f];
    private static readonly float[] Weights3 = [1f, 2f, 1f];
    private readonly float[][] _history;
    private readonly int[] _heads;
    private readonly int[] _counts;
    private readonly float[] _previous;
    private long _previousTimestamp;

    public StateAdaptiveFirFilter(float[] initial)
    {
        _history = new float[initial.Length][];
        _heads = new int[initial.Length];
        _counts = new int[initial.Length];
        _previous = (float[])initial.Clone();
        for (var i = 0; i < initial.Length; i++)
        {
            _history[i] = new float[5];
            _history[i][0] = initial[i];
            _heads[i] = 1 % 5;
            _counts[i] = 1;
        }
        _previousTimestamp = StopwatchCompat.GetTimestamp();
    }

    public float[] Filter(float[] input)
    {
        if (input.Length != _history.Length)
            throw new ArgumentException($"Input shape does not match initial shape. Expected: {_history.Length}, got: {input.Length}");

        var dt = StopwatchCompat.GetElapsedSeconds(ref _previousTimestamp);
        if (dt <= 0.0f)
            return input;

        for (var i = 0; i < input.Length; i++)
        {
            var velocity = Math.Abs(input[i] - _previous[i]) / dt;
            var history = _history[i];
            var head = _heads[i];
            history[head] = input[i];
            head = (head + 1) % 5;
            _heads[i] = head;
            if (_counts[i] < 5)
                _counts[i]++;

            var filtered = velocity switch
            {
                > 0.80f => history[(head - 1 + 5) % 5],
                > 0.25f => TriangularAverage(history, head, _counts[i], 3),
                _ => TriangularAverage(history, head, _counts[i], 5)
            };

            _previous[i] = filtered;
            input[i] = filtered;
        }

        return input;
    }

    private static float TriangularAverage(float[] history, int head, int count, int width)
    {
        var actualWidth = Math.Min(width, count);
        if (actualWidth <= 0)
            return 0f;

        var weights = width == 3 ? Weights3 : Weights5;
        var offset = weights.Length - actualWidth;
        float weightedSum = 0f;
        float totalWeight = 0f;
        var start = (head - actualWidth + history.Length) % history.Length;
        for (var i = 0; i < actualWidth; i++)
        {
            var index = (start + i) % history.Length;
            var weight = weights[i + Math.Max(0, offset)];
            weightedSum += history[index] * weight;
            totalWeight += weight;
        }

        return totalWeight > 0 ? weightedSum / totalWeight : history[(head - 1 + history.Length) % history.Length];
    }
}
