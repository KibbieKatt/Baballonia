using System;

namespace Baballonia.Services.Inference.Filters;

public sealed class ConstantVelocityKalmanFilter : IFilter
{
    private readonly KalmanAxis[] _axes;
    private long _previousTimestamp;

    public ConstantVelocityKalmanFilter(float[] initial)
    {
        _axes = new KalmanAxis[initial.Length];
        for (var i = 0; i < initial.Length; i++)
            _axes[i] = new KalmanAxis(initial[i], 0f, 0.004f, 0.012f);
        _previousTimestamp = StopwatchCompat.GetTimestamp();
    }

    public float[] Filter(float[] input)
    {
        if (input.Length != _axes.Length)
            throw new ArgumentException($"Input shape does not match initial shape. Expected: {_axes.Length}, got: {input.Length}");

        var dt = StopwatchCompat.GetElapsedSeconds(ref _previousTimestamp);
        if (dt <= 0.0f)
            return input;

        for (var i = 0; i < input.Length; i++)
            input[i] = _axes[i].Update(input[i], dt, 1.0f);

        return input;
    }
}

public sealed class AdaptiveKalmanFilter : IFilter
{
    private readonly KalmanAxis[] _axes;
    private long _previousTimestamp;

    public AdaptiveKalmanFilter(float[] initial)
    {
        _axes = new KalmanAxis[initial.Length];
        for (var i = 0; i < initial.Length; i++)
            _axes[i] = new KalmanAxis(initial[i], 0f, 0.006f, 0.010f);
        _previousTimestamp = StopwatchCompat.GetTimestamp();
    }

    public float[] Filter(float[] input)
    {
        if (input.Length != _axes.Length)
            throw new ArgumentException($"Input shape does not match initial shape. Expected: {_axes.Length}, got: {input.Length}");

        var dt = StopwatchCompat.GetElapsedSeconds(ref _previousTimestamp);
        if (dt <= 0.0f)
            return input;

        for (var i = 0; i < input.Length; i++)
        {
            var innovation = Math.Abs(input[i] - _axes[i].PredictedPosition(dt));
            var adaptiveScale = 1.0f + MathF.Min(6.0f, innovation * 18.0f);
            input[i] = _axes[i].Update(input[i], dt, adaptiveScale);
        }

        return input;
    }
}

internal sealed class KalmanAxis
{
    private float _position;
    private float _velocity;
    private float _p00 = 1f;
    private float _p01;
    private float _p10;
    private float _p11 = 1f;
    private readonly float _baseProcessNoise;
    private readonly float _measurementNoise;

    public KalmanAxis(float initialPosition, float initialVelocity, float baseProcessNoise, float measurementNoise)
    {
        _position = initialPosition;
        _velocity = initialVelocity;
        _baseProcessNoise = baseProcessNoise;
        _measurementNoise = measurementNoise;
    }

    public float PredictedPosition(float dt) => _position + _velocity * dt;

    public float Update(float measurement, float dt, float adaptiveScale)
    {
        var xPred = _position + _velocity * dt;
        var vPred = _velocity;

        var q = _baseProcessNoise * adaptiveScale;
        var dt2 = dt * dt;
        var dt3 = dt2 * dt;
        var dt4 = dt2 * dt2;

        var p00 = _p00 + dt * (_p10 + _p01) + dt2 * _p11 + q * dt4 * 0.25f;
        var p01 = _p01 + dt * _p11 + q * dt3 * 0.5f;
        var p10 = _p10 + dt * _p11 + q * dt3 * 0.5f;
        var p11 = _p11 + q * dt2;

        var innovation = measurement - xPred;
        var s = p00 + _measurementNoise;
        var k0 = p00 / s;
        var k1 = p10 / s;

        _position = xPred + k0 * innovation;
        _velocity = vPred + k1 * innovation;

        _p00 = (1 - k0) * p00;
        _p01 = (1 - k0) * p01;
        _p10 = p10 - k1 * p00;
        _p11 = p11 - k1 * p01;
        return _position;
    }
}
