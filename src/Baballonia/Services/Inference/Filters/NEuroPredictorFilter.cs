using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Baballonia.Services.Inference.Filters;

public sealed class NEuroPredictorFilter : IFilter, IDisposable
{
    private const string ModelFileName = "n_euro_predictor.onnx";
    private const string MetaFileName = "n_euro_predictor.training_meta.json";
    private const float MotionEnterThreshold = 0.010f;
    private const float MotionExitThreshold = 0.004f;
    private const float PredictionEnterThreshold = 0.006f;
    private const float PredictionExitThreshold = 0.0025f;
    private const float NeutralRestZone = 0.035f;
    private static readonly int[] EyeGazeIndices = [0, 1, 3, 4];

    private readonly int _valueCount;
    private readonly int _historyFrames;
    private readonly float[] _history;
    private readonly float[] _previousRaw;
    private readonly float[] _rawBuffer;
    private readonly float[] _calmBuffer;
    private float[]? _outputBuffer;
    private readonly bool[] _predictionActive;
    private readonly StateAdaptiveFirFilter _calmFilter;
    private readonly DenseTensor<float>? _inputTensor;
    private readonly InferenceSession? _session;
    private readonly string? _inputName;
    private readonly int _forecastIndex;
    private int _filledFrames;

    public NEuroPredictorFilter(float[] initial)
    {
        _valueCount = initial.Length;
        _previousRaw = (float[])initial.Clone();
        _rawBuffer = (float[])initial.Clone();
        _calmBuffer = (float[])initial.Clone();
        _predictionActive = new bool[initial.Length];
        _calmFilter = new StateAdaptiveFirFilter((float[])initial.Clone());

        try
        {
            var modelPath = Path.Combine(Utils.ModelsDirectory, ModelFileName);
            var metaPath = Path.Combine(Utils.ModelsDirectory, MetaFileName);
            if (!File.Exists(modelPath) || !File.Exists(metaPath))
            {
                _historyFrames = 1;
                _history = (float[])initial.Clone();
                _filledFrames = 1;
                return;
            }

            var meta = JsonSerializer.Deserialize<TrainingMeta>(File.ReadAllText(metaPath));
            _historyFrames = Math.Max(1, meta?.observation_frames ?? 1);
            _forecastIndex = 0;
            _history = new float[_historyFrames * _valueCount];
            SeedHistory(initial);

            var options = new SessionOptions();
            options.AppendExecutionProvider_CPU();
            options.InterOpNumThreads = 1;
            options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");

            _session = new InferenceSession(modelPath, options);
            _inputName = _session.InputMetadata.Keys.First();
            _inputTensor = new DenseTensor<float>(new[] { 1, _historyFrames, _valueCount });
        }
        catch
        {
            _historyFrames = 1;
            _history = (float[])initial.Clone();
            _filledFrames = 1;
            _inputTensor = null;
            _session = null;
            _inputName = null;
        }
    }

    public float[] Filter(float[] input)
    {
        if (input.Length != _valueCount)
            return input;

        if (_session == null || _inputTensor == null || _inputName == null)
            return input;

        Array.Copy(input, _rawBuffer, _valueCount);
        Push(_rawBuffer);
        if (_filledFrames < _historyFrames)
            return input;

        var tensorSpan = _inputTensor.Buffer.Span;
        for (var i = 0; i < _history.Length; i++)
            tensorSpan[i] = _history[i];

        using var results = _session.Run(new List<NamedOnnxValue> { NamedOnnxValue.CreateFromTensor(_inputName, _inputTensor) });
        var outputTensor = results[0].AsTensor<float>();
        ReadOnlySpan<float> outputSpan;
        if (outputTensor is DenseTensor<float> denseOutput)
        {
            outputSpan = denseOutput.Buffer.Span;
        }
        else
        {
            var outputLength = checked((int)outputTensor.Length);
            _outputBuffer ??= new float[outputLength];
            var outputIndex = 0;
            foreach (var value in outputTensor)
                _outputBuffer[outputIndex++] = value;
            outputSpan = new ReadOnlySpan<float>(_outputBuffer, 0, outputLength);
        }

        var start = _forecastIndex * _valueCount;
        if (outputSpan.Length < start + _valueCount)
            return input;

        Array.Copy(_rawBuffer, _calmBuffer, _valueCount);
        _calmFilter.Filter(_calmBuffer);
        Array.Copy(_rawBuffer, input, _valueCount);

        if (_valueCount >= 6)
        {
            foreach (var index in EyeGazeIndices)
            {
                var rawValue = _rawBuffer[index];
                var calmValue = _calmBuffer[index];
                var predictedValue = outputSpan[start + index];
                var delta = Math.Abs(rawValue - _previousRaw[index]);
                var predictionDelta = Math.Abs(predictedValue - rawValue);
                var active = _predictionActive[index];

                active = active
                    ? delta > MotionExitThreshold || predictionDelta > PredictionExitThreshold
                    : delta > MotionEnterThreshold || predictionDelta > PredictionEnterThreshold;

                if (Math.Abs(rawValue) < NeutralRestZone &&
                    Math.Abs(calmValue) < NeutralRestZone &&
                    delta < MotionEnterThreshold)
                {
                    active = false;
                }

                _predictionActive[index] = active;
                input[index] = active
                    ? Lerp(calmValue, predictedValue, ComputePredictionBlend(delta, predictionDelta))
                    : calmValue;

                _previousRaw[index] = rawValue;
            }
        }
        else
        {
            for (var i = 0; i < _valueCount; i++)
                input[i] = outputSpan[start + i];
        }

        return input;
    }

    private void SeedHistory(float[] initial)
    {
        for (var frame = 0; frame < _historyFrames; frame++)
            Array.Copy(initial, 0, _history, frame * _valueCount, _valueCount);
        _filledFrames = _historyFrames;
    }

    private void Push(float[] input)
    {
        var bytesToMove = (_historyFrames - 1) * _valueCount * sizeof(float);
        if (bytesToMove > 0)
            Buffer.BlockCopy(_history, _valueCount * sizeof(float), _history, 0, bytesToMove);

        Buffer.BlockCopy(input, 0, _history, (_historyFrames - 1) * _valueCount * sizeof(float), _valueCount * sizeof(float));
        if (_filledFrames < _historyFrames)
            _filledFrames++;
    }

    public void Dispose()
    {
        _session?.Dispose();
    }

    private static float ComputePredictionBlend(float delta, float predictionDelta)
    {
        var strongestSignal = Math.Max(delta, predictionDelta);
        if (strongestSignal <= MotionExitThreshold)
            return 0f;
        if (strongestSignal >= MotionEnterThreshold)
            return 1f;

        return (strongestSignal - MotionExitThreshold) / (MotionEnterThreshold - MotionExitThreshold);
    }

    private static float Lerp(float a, float b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return a + ((b - a) * t);
    }

    private sealed record TrainingMeta(int observation_frames, int prediction_frames);
}
