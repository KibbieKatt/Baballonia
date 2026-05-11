using Baballonia.Services.events;
using Baballonia.Services.Inference.Enums;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Diagnostics;
using System.Threading;

namespace Baballonia.Services.Inference;

public class EyeProcessingPipeline : DefaultProcessingPipeline, IDisposable
{
    private const int LeftLidIndex = 2;
    private const int RightLidIndex = 5;
    private readonly ILogger<EyeProcessingPipeline> _logger;
    private readonly IEyePipelineEventBus _eyePipelineEventBus;
    private readonly ImageCollector _imageCollector = new();
    private long _runCount;
    private long _nullFrameCount;
    private long _corruptFrameCount;
    private long _transformedFrameCount;
    private long _collectedFrameCount;
    private long _resultCount;
    private long _lastLogTick;
    private long _captureStageTicks;
    private long _captureStageSamples;
    private long _captureStageMaxTicks;
    private long _corruptionStageTicks;
    private long _corruptionStageSamples;
    private long _corruptionStageMaxTicks;
    private long _rawPreviewStageTicks;
    private long _rawPreviewStageSamples;
    private long _rawPreviewStageMaxTicks;
    private long _transformStageTicks;
    private long _transformStageSamples;
    private long _transformStageMaxTicks;
    private long _transformedPreviewStageTicks;
    private long _transformedPreviewStageSamples;
    private long _transformedPreviewStageMaxTicks;
    private long _collectStageTicks;
    private long _collectStageSamples;
    private long _collectStageMaxTicks;
    private long _convertStageTicks;
    private long _convertStageSamples;
    private long _convertStageMaxTicks;
    private long _inferenceStageTicks;
    private long _inferenceStageSamples;
    private long _inferenceStageMaxTicks;
    private long _postProcessStageTicks;
    private long _postProcessStageSamples;
    private long _postProcessStageMaxTicks;
    private long _totalStageTicks;
    private long _totalStageSamples;
    private long _totalStageMaxTicks;

    public EyeProcessingPipeline(ILogger<EyeProcessingPipeline> logger, IEyePipelineEventBus eyePipelineEventBus)
    {
        _logger = logger;
        _eyePipelineEventBus = eyePipelineEventBus;
    }

    public bool StabilizeEyes { get; set; } = true;
    public IFrameCorruptionDetector CorruptionDetector { get; set; } = FrameCorruptionDetectorFactory.Create(CorruptionDetectorModes.StockRow);
    public string ActiveFilterMode { get; set; } = Filters.EyeSmoothingModes.SavitzkyGolayFir;

    public float[]? RunUpdate()
    {
        Interlocked.Increment(ref _runCount);
        var runStartTick = Stopwatch.GetTimestamp();
        Mat? frame = null;
        Mat? transformed = null;
        Mat? collected = null;

        try
        {
            var stageStartTick = Stopwatch.GetTimestamp();
            frame = VideoSource?.GetFrame(ColorType.Gray8);
            RecordStageTiming(ref _captureStageTicks, ref _captureStageSamples, ref _captureStageMaxTicks, Stopwatch.GetTimestamp() - stageStartTick);
            if (frame == null)
            {
                Interlocked.Increment(ref _nullFrameCount);
                LogState("frame-null");
                return null;
            }

            stageStartTick = Stopwatch.GetTimestamp();
            var corruptionResult = CorruptionDetector.IsCorrupted(frame);
            RecordStageTiming(ref _corruptionStageTicks, ref _corruptionStageSamples, ref _corruptionStageMaxTicks, Stopwatch.GetTimestamp() - stageStartTick);
            if (corruptionResult.isCorrupted)
            {
                Interlocked.Increment(ref _corruptFrameCount);
                LogState("corrupt", frame);
                return null;
            }

            stageStartTick = Stopwatch.GetTimestamp();
            _eyePipelineEventBus.Publish(new EyePipelineEvents.NewFrameEvent(frame));
            RecordStageTiming(ref _rawPreviewStageTicks, ref _rawPreviewStageSamples, ref _rawPreviewStageMaxTicks, Stopwatch.GetTimestamp() - stageStartTick);

            stageStartTick = Stopwatch.GetTimestamp();
            transformed = ImageTransformer?.Apply(frame);
            RecordStageTiming(ref _transformStageTicks, ref _transformStageSamples, ref _transformStageMaxTicks, Stopwatch.GetTimestamp() - stageStartTick);
            if (transformed == null)
                return null;
            Interlocked.Increment(ref _transformedFrameCount);

            stageStartTick = Stopwatch.GetTimestamp();
            _eyePipelineEventBus.Publish(new EyePipelineEvents.NewTransformedFrameEvent(transformed));
            RecordStageTiming(ref _transformedPreviewStageTicks, ref _transformedPreviewStageSamples, ref _transformedPreviewStageMaxTicks, Stopwatch.GetTimestamp() - stageStartTick);

            stageStartTick = Stopwatch.GetTimestamp();
            collected = _imageCollector.Apply(transformed);
            RecordStageTiming(ref _collectStageTicks, ref _collectStageSamples, ref _collectStageMaxTicks, Stopwatch.GetTimestamp() - stageStartTick);
            if (collected == null)
                return null;
            Interlocked.Increment(ref _collectedFrameCount);

            if (InferenceService == null)
                return null;

            stageStartTick = Stopwatch.GetTimestamp();
            ImageConverter?.Convert(collected, InferenceService.GetInputTensor());
            RecordStageTiming(ref _convertStageTicks, ref _convertStageSamples, ref _convertStageMaxTicks, Stopwatch.GetTimestamp() - stageStartTick);

            stageStartTick = Stopwatch.GetTimestamp();
            var inferenceResult = InferenceService.Run();
            RecordStageTiming(ref _inferenceStageTicks, ref _inferenceStageSamples, ref _inferenceStageMaxTicks, Stopwatch.GetTimestamp() - stageStartTick);
            if (inferenceResult == null)
                return null;

            var traceSubscribersActive = _eyePipelineEventBus.HasSubscribers<EyePipelineEvents.TraceResultEvent>();
            float[]? rawInferenceResult = null;
            if (traceSubscribersActive)
                rawInferenceResult = (float[])inferenceResult.Clone();

            stageStartTick = Stopwatch.GetTimestamp();
            if (Filter != null)
            {
                inferenceResult = Filter.Filter(inferenceResult);
                if (rawInferenceResult != null)
                    RestoreRawLids(rawInferenceResult, inferenceResult);
            }

            float[]? filteredInferenceResult = null;
            if (traceSubscribersActive)
                filteredInferenceResult = (float[])inferenceResult.Clone();
            ProcessExpressions(ref inferenceResult);
            float[]? processedResult = null;
            if (traceSubscribersActive)
                processedResult = (float[])inferenceResult.Clone();

            _eyePipelineEventBus.Publish(new EyePipelineEvents.NewFilteredResultEvent(inferenceResult));
            if (traceSubscribersActive && rawInferenceResult != null && filteredInferenceResult != null && processedResult != null)
            {
                _eyePipelineEventBus.Publish(
                    new EyePipelineEvents.TraceResultEvent(
                        rawInferenceResult,
                        filteredInferenceResult,
                        processedResult,
                        VideoSource?.LastDeliveredFrameSequence ?? 0,
                        VideoSource?.LastDeliveredFrameTimestampTick ?? 0,
                        Stopwatch.GetTimestamp(),
                        VideoSource?.SourceDescription ?? "unknown",
                        ActiveFilterMode));
            }
            RecordStageTiming(ref _postProcessStageTicks, ref _postProcessStageSamples, ref _postProcessStageMaxTicks, Stopwatch.GetTimestamp() - stageStartTick);
            Interlocked.Increment(ref _resultCount);
            LogState("result", transformed);

            return inferenceResult;
        }
        finally
        {
            collected?.Dispose();
            transformed?.Dispose();
            frame?.Dispose();
            RecordStageTiming(ref _totalStageTicks, ref _totalStageSamples, ref _totalStageMaxTicks, Stopwatch.GetTimestamp() - runStartTick);
        }
    }

    private void LogState(string stage, Mat? frame = null)
    {
        var nowTick = Environment.TickCount64;
        var lastTick = Interlocked.Read(ref _lastLogTick);
        if (nowTick - lastTick < 2000)
            return;

        Interlocked.Exchange(ref _lastLogTick, nowTick);
        var captureSamples = Interlocked.Read(ref _captureStageSamples);
        var corruptionSamples = Interlocked.Read(ref _corruptionStageSamples);
        var inferenceSamples = Interlocked.Read(ref _inferenceStageSamples);
        var totalSamples = Interlocked.Read(ref _totalStageSamples);
        _logger.LogInformation(
            "EyePipeline {Stage}: runs={Runs} nullFrames={NullFrames} corrupt={Corrupt} transformed={Transformed} collected={Collected} results={Results} videoSource={VideoSource} inference={HasInference} detector={Detector} frame={Width}x{Height}x{Channels} stageMeanMs capture={CaptureMeanMs:F3} corruption={CorruptionMeanMs:F3} rawPreview={RawPreviewMeanMs:F3} transform={TransformMeanMs:F3} transformedPreview={TransformedPreviewMeanMs:F3} collect={CollectMeanMs:F3} convert={ConvertMeanMs:F3} inference={InferenceMeanMs:F3} post={PostMeanMs:F3} total={TotalMeanMs:F3} stageMaxMs corruption={CorruptionMaxMs:F3} inference={InferenceMaxMs:F3} total={TotalMaxMs:F3} stageSamples capture={CaptureSamples} corruption={CorruptionSamples} inference={InferenceSamples} total={TotalSamples}",
            stage,
            Interlocked.Read(ref _runCount),
            Interlocked.Read(ref _nullFrameCount),
            Interlocked.Read(ref _corruptFrameCount),
            Interlocked.Read(ref _transformedFrameCount),
            Interlocked.Read(ref _collectedFrameCount),
            Interlocked.Read(ref _resultCount),
            VideoSource?.GetType().Name ?? "null",
            InferenceService != null,
            CorruptionDetector.Mode,
            frame?.Width ?? 0,
            frame?.Height ?? 0,
            frame?.Channels() ?? 0,
            TakeStageMeanMs(ref _captureStageTicks, ref _captureStageSamples),
            TakeStageMeanMs(ref _corruptionStageTicks, ref _corruptionStageSamples),
            TakeStageMeanMs(ref _rawPreviewStageTicks, ref _rawPreviewStageSamples),
            TakeStageMeanMs(ref _transformStageTicks, ref _transformStageSamples),
            TakeStageMeanMs(ref _transformedPreviewStageTicks, ref _transformedPreviewStageSamples),
            TakeStageMeanMs(ref _collectStageTicks, ref _collectStageSamples),
            TakeStageMeanMs(ref _convertStageTicks, ref _convertStageSamples),
            TakeStageMeanMs(ref _inferenceStageTicks, ref _inferenceStageSamples),
            TakeStageMeanMs(ref _postProcessStageTicks, ref _postProcessStageSamples),
            TakeStageMeanMs(ref _totalStageTicks, ref _totalStageSamples),
            TakeStageMaxMs(ref _corruptionStageMaxTicks),
            TakeStageMaxMs(ref _inferenceStageMaxTicks),
            TakeStageMaxMs(ref _totalStageMaxTicks),
            captureSamples,
            corruptionSamples,
            inferenceSamples,
            totalSamples);
    }

    private static void RecordStageTiming(ref long totalTicksField, ref long sampleCountField, ref long maxTicksField, long elapsedTicks)
    {
        Interlocked.Add(ref totalTicksField, elapsedTicks);
        Interlocked.Increment(ref sampleCountField);

        while (true)
        {
            var currentMax = Volatile.Read(ref maxTicksField);
            if (elapsedTicks <= currentMax)
                return;

            if (Interlocked.CompareExchange(ref maxTicksField, elapsedTicks, currentMax) == currentMax)
                return;
        }
    }

    private static double TakeStageMeanMs(ref long totalTicksField, ref long sampleCountField)
    {
        var ticks = Interlocked.Exchange(ref totalTicksField, 0);
        var samples = Interlocked.Exchange(ref sampleCountField, 0);
        if (samples <= 0)
            return 0;

        return TicksToMilliseconds(ticks / (double)samples);
    }

    private static double TakeStageMaxMs(ref long maxTicksField)
    {
        return TicksToMilliseconds(Interlocked.Exchange(ref maxTicksField, 0));
    }

    private static double TicksToMilliseconds(double ticks)
    {
        return ticks * 1000.0 / Stopwatch.Frequency;
    }

    private static void RestoreRawLids(float[] rawInferenceResult, float[] filteredInferenceResult)
    {
        if (rawInferenceResult.Length <= RightLidIndex || filteredInferenceResult.Length <= RightLidIndex)
            return;

        filteredInferenceResult[LeftLidIndex] = rawInferenceResult[LeftLidIndex];
        filteredInferenceResult[RightLidIndex] = rawInferenceResult[RightLidIndex];
    }

    private bool ProcessExpressions(ref float[] arKitExpressions)
    {
        if (arKitExpressions.Length < Utils.EyeRawExpressions)
            return false;

        const float mulV = 2.0f;
        const float mulY = 2.0f;

        var leftPitch = arKitExpressions[0] * mulY - mulY / 2;
        var leftYaw = arKitExpressions[1] * mulV - mulV / 2;
        var leftLid = 1 - arKitExpressions[2];

        var rightPitch = arKitExpressions[3] * mulY - mulY / 2;
        var rightYaw = arKitExpressions[4] * mulV - mulV / 2;
        var rightLid = 1 - arKitExpressions[5];

        var eyeY = (leftPitch * leftLid + rightPitch * rightLid) / (leftLid + rightLid);

        var leftEyeYawCorrected = rightYaw * (1 - leftLid) + leftYaw * leftLid;
        var rightEyeYawCorrected = leftYaw * (1 - rightLid) + rightYaw * rightLid;

        if (StabilizeEyes)
        {
            var rawConvergence = (rightEyeYawCorrected - leftEyeYawCorrected) / 2.0f;
            var convergence = Math.Max(rawConvergence, 0.0f); // We clamp the value here to avoid accidental divergence, as the model sometimes decides that's a thing

            var averagedYaw = (rightEyeYawCorrected + leftEyeYawCorrected) / 2.0f;

            leftEyeYawCorrected = averagedYaw - convergence;
            rightEyeYawCorrected = averagedYaw + convergence;
        }

        // [left pitch, left yaw, left lid...
        arKitExpressions[0] = rightEyeYawCorrected; // left pitch
        arKitExpressions[1] = eyeY;                 // left yaw
        arKitExpressions[2] = rightLid;             // left lid
        arKitExpressions[3] = leftEyeYawCorrected;  // right pitch
        arKitExpressions[4] = eyeY;                 // right yaw
        arKitExpressions[5] = leftLid;              // right lid

        return true;
    }


    public void Dispose()
    {
        TryDisposeObject(VideoSource);
        TryDisposeObject(ImageTransformer);
        TryDisposeObject(ImageConverter);
        TryDisposeObject(InferenceService);
        TryDisposeObject(Filter);
        TryDisposeObject(CorruptionDetector);
        TryDisposeObject(_imageCollector);
    }

    private void TryDisposeObject(object? obj)
    {
        (obj as IDisposable)?.Dispose();
    }
}
