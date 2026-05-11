using System;
namespace Baballonia.Services.Inference.Filters;

public class OneEuroFilter : IFilter
{
    private readonly float[] _minCutoff;
    private readonly float[] _beta;
    private readonly float[] _dCutoff;
    private readonly float[] _xPrev;
    private readonly float[] _dxPrev;
    private long _previousTimestamp;

    public OneEuroFilter(float[] x0, float minCutoff = 1.0f, float beta = 0.0f)
    {
        var length = x0.Length;
        _minCutoff = CreateFilledArray(length, minCutoff);
        _beta = CreateFilledArray(length, beta);
        _dCutoff = CreateFilledArray(length, 1.0f);
        _xPrev = (float[])x0.Clone();
        _dxPrev = CreateFilledArray(length, 0.0f);
        _previousTimestamp = StopwatchCompat.GetTimestamp();
    }

    public float[] Filter(float[] input)
    {
        if (input.Length != _xPrev.Length)
            throw new ArgumentException($"Input shape does not match initial shape. Expected: {_xPrev.Length}, got: {input.Length}");

        var elapsedSeconds = StopwatchCompat.GetElapsedSeconds(ref _previousTimestamp);

        if (elapsedSeconds <= 0.0f)
        {
            return input;
        }

        for (var i = 0; i < input.Length; i++)
        {
            var derivative = (input[i] - _xPrev[i]) / elapsedSeconds;
            var derivativeAlpha = SmoothingFactor(elapsedSeconds, _dCutoff[i]);
            var derivativeHat = ExponentialSmoothing(derivativeAlpha, derivative, _dxPrev[i]);
            var cutoff = _minCutoff[i] + _beta[i] * Math.Abs(derivativeHat);
            var valueAlpha = SmoothingFactor(elapsedSeconds, cutoff);
            var filteredValue = ExponentialSmoothing(valueAlpha, input[i], _xPrev[i]);

            _dxPrev[i] = derivativeHat;
            _xPrev[i] = filteredValue;
            input[i] = filteredValue;
        }
        return input;
    }

    private static float[] CreateFilledArray(int length, float value)
    {
        var arr = new float[length];
        for (var i = 0; i < length; i++)
            arr[i] = value;
        return arr;
    }

    private static float SmoothingFactor(float elapsedSeconds, float cutoff)
    {
        var r = 2 * (float)Math.PI * cutoff * elapsedSeconds;
        return r / (r + 1);
    }

    private static float ExponentialSmoothing(float alpha, float current, float previous)
        => alpha * current + (1 - alpha) * previous;
}
