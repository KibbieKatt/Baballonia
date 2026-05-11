using Baballonia.Services.events;
using Baballonia.Services.Inference;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Baballonia;

namespace Baballonia.Services;

public class ProcessingLoopService : IDisposable
{
    private static readonly TimeSpan ActiveLoopCadence = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan IdleLoopCadence = TimeSpan.FromMilliseconds(20);
    private static readonly TimeSpan FrameDrivenWaitTimeout = TimeSpan.FromMilliseconds(20);

    public record struct Expressions(float[]? FaceExpression, float[]? EyeExpression, long ProducedAtTick = 0, long EyeFrameCapturedAtTick = 0);

    public event Action<Expressions>? ExpressionChangeEvent;

    private readonly ILogger<ProcessingLoopService> _logger;
    private readonly FaceProcessingPipeline _faceProcessingPipeline;
    private readonly FacePipelineManager _facePipelineManager;
    private readonly IFacePipelineEventBus _facePipelineEventBus;
    private readonly EyeProcessingPipeline _eyeProcessingPipeline;
    private readonly EyePipelineManager _eyePipelineManager;
    private readonly IEyePipelineEventBus _eyePipelineEventBus;
    private readonly CancellationTokenSource _loopCancellationTokenSource = new();
    private readonly Task _loopTask;
    private volatile bool _isPaused;
    private long _timerTickCount;
    private long _eyeTickCount;
    private long _eyeSkippedCount;
    private long _eyeDuplicateFrameSkippedCount;
    private long _lastWaitedFaceFrameSequence;
    private long _lastProcessedEyeFrameSequence;
    private long _lastWaitedEyeFrameSequence;
    private long _lastStatusLogTick;

    public ProcessingLoopService(
        ILogger<ProcessingLoopService> logger,
        EyeProcessingPipeline eyeProcessingPipeline, FaceProcessingPipeline faceProcessingPipeline,
        IFacePipelineEventBus facePipelineEventBus, IEyePipelineEventBus eyePipelineEventBus,
        FacePipelineManager facePipelineManager, EyePipelineManager eyePipelineManager)
    {
        _logger = logger;
        _eyeProcessingPipeline = eyeProcessingPipeline;
        _faceProcessingPipeline = faceProcessingPipeline;
        _facePipelineEventBus = facePipelineEventBus;
        _eyePipelineEventBus = eyePipelineEventBus;
        _facePipelineManager = facePipelineManager;
        _eyePipelineManager = eyePipelineManager;
        _loopTask = Task.Factory.StartNew(() => RunLoopAsync(_loopCancellationTokenSource.Token),
            _loopCancellationTokenSource.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default).Unwrap();
    }

    private Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            TryElevateProcessingThreadPriority();
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_isPaused)
                {
                    HighResolutionPacer.WaitUntil(Stopwatch.GetTimestamp(), IdleLoopCadence, cancellationToken);
                    continue;
                }

                var waitResult = WaitForActiveFrameIfSupported(cancellationToken);
                if (waitResult == EyeFrameWaitResult.TimedOut)
                    continue;

                var iterationStart = Stopwatch.GetTimestamp();
                TimerEvent();
                if (waitResult == EyeFrameWaitResult.NotUsed)
                {
                    var cadence = HasActivePipeline() ? ActiveLoopCadence : IdleLoopCadence;
                    HighResolutionPacer.WaitUntil(iterationStart, cadence, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        return Task.CompletedTask;
    }

    private EyeFrameWaitResult WaitForActiveFrameIfSupported(CancellationToken cancellationToken)
    {
        var faceWaitResult = WaitForSourceFrameIfSupported(
            _faceProcessingPipeline.VideoSource,
            _faceProcessingPipeline.InferenceService != null,
            ref _lastWaitedFaceFrameSequence,
            cancellationToken);
        if (faceWaitResult != EyeFrameWaitResult.NotUsed)
            return faceWaitResult;

        return WaitForSourceFrameIfSupported(
            _eyeProcessingPipeline.VideoSource,
            _eyeProcessingPipeline.InferenceService != null,
            ref _lastWaitedEyeFrameSequence,
            cancellationToken);
    }

    private static EyeFrameWaitResult WaitForSourceFrameIfSupported(
        IVideoSource? videoSource,
        bool inferenceActive,
        ref long lastWaitedFrameSequence,
        CancellationToken cancellationToken)
    {
        if (!inferenceActive || videoSource is not IFrameWaitSource frameWaitSource)
            return EyeFrameWaitResult.NotUsed;

        var lastSeenSequence = Interlocked.Read(ref lastWaitedFrameSequence);
        var currentSequence = videoSource.FrameSequence;
        if (currentSequence > lastSeenSequence)
        {
            Interlocked.Exchange(ref lastWaitedFrameSequence, currentSequence);
            return EyeFrameWaitResult.FrameReady;
        }

        if (!frameWaitSource.WaitForFrameAfter(lastSeenSequence, FrameDrivenWaitTimeout, cancellationToken))
            return EyeFrameWaitResult.TimedOut;

        var signaledSequence = videoSource.FrameSequence;
        if (signaledSequence > 0)
            Interlocked.Exchange(ref lastWaitedFrameSequence, signaledSequence);

        return EyeFrameWaitResult.FrameReady;
    }

    private static void TryElevateProcessingThreadPriority()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            Thread.CurrentThread.Priority = ThreadPriority.AboveNormal;
        }
        catch
        {
            // Best-effort only.
        }
    }

    private bool HasActivePipeline()
    {
        return (_faceProcessingPipeline.VideoSource != null && _faceProcessingPipeline.InferenceService != null) ||
               (_eyeProcessingPipeline.VideoSource != null && _eyeProcessingPipeline.InferenceService != null);
    }

    private void TimerEvent()
    {
        Interlocked.Increment(ref _timerTickCount);
        var expressions = new Expressions(null, null, Stopwatch.GetTimestamp());

        if (_faceProcessingPipeline.VideoSource != null && _faceProcessingPipeline.InferenceService != null)
        {
            try
            {
                var faceExpression = _faceProcessingPipeline.RunUpdate();
                if (faceExpression != null)
                    expressions.FaceExpression = faceExpression;
            }
            catch (Exception ex)
            {
                _logger.LogError("Unexpected exception in Face Tracking pipeline, stopping... : {}", ex);
                _facePipelineManager.StopCamera();
                _facePipelineEventBus.Publish(new FacePipelineEvents.ExceptionEvent(ex));
            }
        }

        if (_eyeProcessingPipeline.VideoSource != null && _eyeProcessingPipeline.InferenceService != null)
        {
            Interlocked.Increment(ref _eyeTickCount);
            try
            {
                var frameSequence = _eyeProcessingPipeline.VideoSource.FrameSequence;
                if (frameSequence > 0 && frameSequence == Interlocked.Read(ref _lastProcessedEyeFrameSequence))
                {
                    Interlocked.Increment(ref _eyeDuplicateFrameSkippedCount);
                    LogLoopStatus();
                    return;
                }

                var eyeExpression = _eyeProcessingPipeline.RunUpdate();
                if (frameSequence > 0)
                    Interlocked.Exchange(ref _lastProcessedEyeFrameSequence, frameSequence);

                if (eyeExpression != null)
                {
                    expressions.EyeExpression = eyeExpression;
                    expressions.EyeFrameCapturedAtTick = _eyeProcessingPipeline.VideoSource?.LatestFrameTimestampTick ?? 0;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("Unexpected exception in Eye Tracking pipeline, stopping... : {}", ex);
                _eyePipelineManager.StopAllCameras();
                _eyePipelineEventBus.Publish(new EyePipelineEvents.ExceptionEvent(ex));
            }
        }
        else
        {
            Interlocked.Increment(ref _eyeSkippedCount);
        }

        if (expressions.FaceExpression != null || expressions.EyeExpression != null)
            ExpressionChangeEvent?.Invoke(expressions);

        LogLoopStatus();
    }

    private void LogLoopStatus()
    {
        var nowTick = Environment.TickCount64;
        var lastTick = Interlocked.Read(ref _lastStatusLogTick);
        if (nowTick - lastTick < 2000)
            return;

        Interlocked.Exchange(ref _lastStatusLogTick, nowTick);
        _logger.LogInformation(
            "ProcessingLoop paused={Paused} ticks={Ticks} eyeTicks={EyeTicks} eyeSkipped={EyeSkipped} eyeDuplicateSkipped={EyeDuplicateSkipped} eyeVideoSource={EyeVideoSource} eyeInference={HasEyeInference}",
            _isPaused,
            Interlocked.Read(ref _timerTickCount),
            Interlocked.Read(ref _eyeTickCount),
            Interlocked.Read(ref _eyeSkippedCount),
            Interlocked.Read(ref _eyeDuplicateFrameSkippedCount),
            _eyeProcessingPipeline.VideoSource?.GetType().Name ?? "null",
            _eyeProcessingPipeline.InferenceService != null);
    }

    public void Start()
    {
        _isPaused = false;
    }

    public void Pause()
    {
        _isPaused = true;
    }

    public void Dispose()
    {
        _loopCancellationTokenSource.Cancel();
        try
        {
            _loopTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Ignore loop shutdown exceptions.
        }

        _loopCancellationTokenSource.Dispose();
        _faceProcessingPipeline.VideoSource?.Dispose();
        _eyeProcessingPipeline.VideoSource?.Dispose();
    }

    private enum EyeFrameWaitResult
    {
        NotUsed,
        FrameReady,
        TimedOut
    }
}
