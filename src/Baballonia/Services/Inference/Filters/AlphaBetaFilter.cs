using System;

namespace Baballonia.Services.Inference.Filters;

public sealed class AlphaBetaFilter : IFilter
{
    private readonly float[] _position;
    private readonly float[] _velocity;
    private readonly float _alpha;
    private readonly float _beta;
    private long _previousTimestamp;

    public AlphaBetaFilter(float[] initial, float alpha = 0.82f, float beta = 0.18f)
    {
        _position = (float[])initial.Clone();
        _velocity = new float[initial.Length];
        _alpha = alpha;
        _beta = beta;
        _previousTimestamp = StopwatchCompat.GetTimestamp();
    }

    public float[] Filter(float[] input)
    {
        if (input.Length != _position.Length)
            throw new ArgumentException($"Input shape does not match initial shape. Expected: {_position.Length}, got: {input.Length}");

        var dt = StopwatchCompat.GetElapsedSeconds(ref _previousTimestamp);
        if (dt <= 0.0f)
            return input;

        for (var i = 0; i < input.Length; i++)
        {
            var prediction = _position[i] + _velocity[i] * dt;
            var residual = input[i] - prediction;
            _position[i] = prediction + _alpha * residual;
            _velocity[i] += (_beta / dt) * residual;
            input[i] = _position[i];
        }

        return input;
    }
}
