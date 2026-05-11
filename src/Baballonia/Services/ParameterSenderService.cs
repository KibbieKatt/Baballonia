using Baballonia.Contracts;
using Baballonia.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OscCore;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.Services;

public class ParameterSenderService : BackgroundService
{
    private const string VrcftFrameReadyAddress = "/vrcft/babble/frameReady";
    private readonly VrcftModuleSendService _vrcftModuleSendService;
    private readonly DfrSendService _dfrSendService;
    private readonly ILocalSettingsService _localSettingsService;
    private readonly ICalibrationService _calibrationService;
    private readonly ProcessingLoopService _processingLoopService;
    private readonly ILogger<ParameterSenderService> _logger;
    private readonly object _expressionLock = new();
    private readonly SemaphoreSlim _pendingExpressionsSignal = new(0, 1);

    private string _prefix = "";
    private bool _sendNativeVrcEyeTracking;
    private bool _useDfr;
    private ProcessingLoopService.Expressions _latestExpressions;
    private bool _hasPendingExpressions;
    private long _nextSettingsRefreshTick;
    private long _lastLatencyLogTick;
    private long _oscLatencyTicks;
    private long _oscLatencySamples;
    private long _oscLatencyMaxTicks;
    private long _currentExpressionProducedTick;
    private long _cameraToOscLatencyTicks;
    private long _cameraToOscLatencySamples;
    private long _cameraToOscLatencyMaxTicks;
    private long _currentEyeFrameCapturedTick;
    private int _vrcftFrameSyncSequence;
    private readonly List<OscMessage> _vrcftQueue = new(64);
    private readonly List<OscMessage> _dfrQueue = new(16);

    // Expression parameter names
    private readonly Dictionary<string, string> _eyeExpressionMap = new()
    {
        { "LeftEyeX", "/LeftEyeX" },
        { "LeftEyeY", "/LeftEyeY" },
        { "LeftEyeLid", "/LeftEyeLid" },
        //{ "LeftEyeWiden", "/LeftEyeWiden" },
        //{ "LeftEyeLower", "/LeftEyeLower" },
        //{ "LeftEyeBrow", "/LeftEyeBrow" },
        { "RightEyeX", "/RightEyeX" },
        { "RightEyeY", "/RightEyeY" },
        { "RightEyeLid", "/RightEyeLid" },
        //{ "RightEyeWiden", "/RightEyeWiden" },
        //{ "RightEyeLower", "/RightEyeLower" },
        //{ "RightEyeBrow", "/RightEyeBrow" },
    };

    public readonly Dictionary<string, string> FaceExpressionMap = new()
    {
        { "CheekPuffLeft", "/cheekPuffLeft" },
        { "CheekPuffRight", "/cheekPuffRight" },
        { "CheekSuckLeft", "/cheekSuckLeft" },
        { "CheekSuckRight", "/cheekSuckRight" },
        { "JawOpen", "/jawOpen" },
        { "JawForward", "/jawForward" },
        { "JawLeft", "/jawLeft" },
        { "JawRight", "/jawRight" },
        { "NoseSneerLeft", "/noseSneerLeft" },
        { "NoseSneerRight", "/noseSneerRight" },
        { "MouthFunnel", "/mouthFunnel" },
        { "MouthPucker", "/mouthPucker" },
        { "MouthLeft", "/mouthLeft" },
        { "MouthRight", "/mouthRight" },
        { "MouthRollUpper", "/mouthRollUpper" },
        { "MouthRollLower", "/mouthRollLower" },
        { "MouthShrugUpper", "/mouthShrugUpper" },
        { "MouthShrugLower", "/mouthShrugLower" },
        { "MouthClose", "/mouthClose" },
        { "MouthSmileLeft", "/mouthSmileLeft" },
        { "MouthSmileRight", "/mouthSmileRight" },
        { "MouthFrownLeft", "/mouthFrownLeft" },
        { "MouthFrownRight", "/mouthFrownRight" },
        { "MouthDimpleLeft", "/mouthDimpleLeft" },
        { "MouthDimpleRight", "/mouthDimpleRight" },
        { "MouthUpperUpLeft", "/mouthUpperUpLeft" },
        { "MouthUpperUpRight", "/mouthUpperUpRight" },
        { "MouthLowerDownLeft", "/mouthLowerDownLeft" },
        { "MouthLowerDownRight", "/mouthLowerDownRight" },
        { "MouthPressLeft", "/mouthPressLeft" },
        { "MouthPressRight", "/mouthPressRight" },
        { "MouthStretchLeft", "/mouthStretchLeft" },
        { "MouthStretchRight", "/mouthStretchRight" },
        { "TongueOut", "/tongueOut" },
        { "TongueUp", "/tongueUp" },
        { "TongueDown", "/tongueDown" },
        { "TongueLeft", "/tongueLeft" },
        { "TongueRight", "/tongueRight" },
        { "TongueRoll", "/tongueRoll" },
        { "TongueBendDown", "/tongueBendDown" },
        { "TongueCurlUp", "/tongueCurlUp" },
        { "TongueSquish", "/tongueSquish" },
        { "TongueFlat", "/tongueFlat" },
        { "TongueTwistLeft", "/tongueTwistLeft" },
        { "TongueTwistRight", "/tongueTwistRight" }
    };

    public ParameterSenderService(
        VrcftModuleSendService vrcftModuleSendService,
        DfrSendService dfrSendService,
        ILocalSettingsService localSettingsService,
        ICalibrationService calibrationService,
        ProcessingLoopService processingLoopService,
        ILogger<ParameterSenderService> logger)
    {
        this._vrcftModuleSendService = vrcftModuleSendService;
        this._dfrSendService = dfrSendService;
        this._localSettingsService = localSettingsService;
        this._calibrationService = calibrationService;
        this._processingLoopService = processingLoopService;
        this._logger = logger;

        RefreshRuntimeSettings(force: true);
        _processingLoopService.ExpressionChangeEvent += ExpressionUpdateHandler;
    }

    protected override async Task ExecuteAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Starting Parameter Sender Service...");
        _logger.LogDebug("OSC parameter mapping initialized with {EyeCount} eye expressions and {FaceCount} face expressions",
            _eyeExpressionMap.Count, FaceExpressionMap.Count);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    RefreshRuntimeSettings();
                    if (!_hasPendingExpressions)
                    {
                        await _pendingExpressionsSignal.WaitAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
                        RefreshRuntimeSettings();
                    }

                    DrainPendingExpressions();
                    await SendAndClearQueue(cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Ignoring non-fatal exception in parameter sender loop");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void ExpressionUpdateHandler(ProcessingLoopService.Expressions expressions)
    {
        var shouldSignal = false;
        lock (_expressionLock)
        {
            _latestExpressions = expressions;
            if (!_hasPendingExpressions)
            {
                _hasPendingExpressions = true;
                shouldSignal = true;
            }
        }

        if (shouldSignal && _pendingExpressionsSignal.CurrentCount == 0)
            _pendingExpressionsSignal.Release();
    }

    private void RefreshRuntimeSettings(bool force = false)
    {
        var now = Environment.TickCount64;
        if (!force && now < Interlocked.Read(ref _nextSettingsRefreshTick))
            return;

        _prefix = _localSettingsService.ReadSetting<string>("AppSettings_OSCPrefix", "") ?? "";
        _sendNativeVrcEyeTracking = _localSettingsService.ReadSetting<bool>("VRC_UseNativeTracking");
        _useDfr = _localSettingsService.ReadSetting<bool>("AppSettings_UseDFR");
        Interlocked.Exchange(ref _nextSettingsRefreshTick, now + 500);
    }

    private void DrainPendingExpressions()
    {
        ProcessingLoopService.Expressions expressions;
        lock (_expressionLock)
        {
            if (!_hasPendingExpressions)
                return;

            expressions = _latestExpressions;
            _hasPendingExpressions = false;
        }

        _currentExpressionProducedTick = expressions.ProducedAtTick;
        _currentEyeFrameCapturedTick = expressions.EyeFrameCapturedAtTick;

        if (expressions.EyeExpression != null)
            ProcessEyeExpressionData(expressions.EyeExpression);
        if (expressions.FaceExpression != null)
            ProcessFaceExpressionData(expressions.FaceExpression);
    }

    private void ProcessEyeExpressionData(float[] expressions)
    {
        if (expressions is null) return;
        if (expressions.Length == 0) return;

        var expressionIndex = 0;
        foreach (var eyeElement in _eyeExpressionMap)
        {
            if (expressionIndex >= expressions.Length)
                break;

            var weight = SanitizeEyeValue(eyeElement.Key, expressions[expressionIndex++]);
            var settings = _calibrationService.GetExpressionSettings(eyeElement.Key);

            var msg = new OscMessage(_prefix + eyeElement.Value,
                ClampFinite(weight.Remap(settings.Lower, settings.Upper, settings.Min, settings.Max), settings.Min, settings.Max));
            _vrcftQueue.Add(msg);
        }

        if (_useDfr)
            ProcessNativeVrcEyeTracking(expressions, _dfrQueue);

        if (_sendNativeVrcEyeTracking)
            ProcessNativeVrcEyeTracking(expressions, _vrcftQueue);
    }

    private void ProcessNativeVrcEyeTracking(float[] expressions, List<OscMessage> queue)
    {
        var leftEyeX = SanitizeEyeValue("LeftEyeX", expressions[0]);
        var leftEyeY = SanitizeEyeValue("LeftEyeY", expressions[1]);
        var leftEyeLid = SanitizeEyeValue("LeftEyeLid", expressions[2]);
        var rightEyeX = SanitizeEyeValue("RightEyeX", expressions[3]);
        var rightEyeY = SanitizeEyeValue("RightEyeY", expressions[4]);
        var rightEyeLid = SanitizeEyeValue("RightEyeLid", expressions[5]);

        var leftEyeLidSettings = _calibrationService.GetExpressionSettings("LeftEyeLid");
        var rightEyeLidSettings = _calibrationService.GetExpressionSettings("RightEyeLid");
        var weightedLeftEyeLid = leftEyeLid.Remap(leftEyeLidSettings.Lower, leftEyeLidSettings.Upper, leftEyeLidSettings.Min, leftEyeLidSettings.Max);
        var weightedRightEyeLid = rightEyeLid.Remap(rightEyeLidSettings.Lower, rightEyeLidSettings.Upper, rightEyeLidSettings.Min, rightEyeLidSettings.Max);
        var averageLid = (weightedLeftEyeLid + weightedRightEyeLid) / 2f;
        queue.Add(new OscMessage("/tracking/eye/EyesClosedAmount", 1f - Math.Clamp(averageLid, 0f, 1f)));

        // Convert normalized eye positions to angles
        const float maxEyeAngle = 45f;
        leftEyeX *= maxEyeAngle;
        leftEyeY *= -maxEyeAngle; // Negative because Y is inverted (up is negative pitch)
        rightEyeX *= maxEyeAngle;
        rightEyeY *= -maxEyeAngle; // Negative because Y is inverted (up is negative pitch)
        queue.Add(new OscMessage("/tracking/eye/LeftRightPitchYaw", leftEyeY, rightEyeX, rightEyeY, leftEyeX));
    }

    private static float SanitizeEyeValue(string key, float value)
    {
        if (!float.IsFinite(value))
            return 0f;

        return key switch
        {
            "LeftEyeLid" or "RightEyeLid" => Math.Clamp(value, 0f, 1f),
            "LeftEyeX" or "LeftEyeY" or "RightEyeX" or "RightEyeY" => Math.Clamp(value, -1f, 1f),
            _ => value
        };
    }

    private static float ClampFinite(float value, float min, float max)
    {
        if (!float.IsFinite(value))
            return min;

        return Math.Clamp(value, min, max);
    }

    private void ProcessFaceExpressionData(float[] expressions)
    {
        if (expressions == null) return;
        if (expressions.Length == 0) return;

        var expressionIndex = 0;
        foreach (var faceElement in FaceExpressionMap)
        {
            if (expressionIndex >= expressions.Length)
                break;

            var weight = expressions[expressionIndex++];
            var settings = _calibrationService.GetExpressionSettings(faceElement.Key);

            var msg = new OscMessage(_prefix + faceElement.Value,
                Math.Clamp(
                    weight.Remap(settings.Lower, settings.Upper, settings.Min, settings.Max),
                    settings.Min,
                    settings.Max));
            _vrcftQueue.Add(msg);
        }
    }

    private async Task SendAndClearQueue(CancellationToken cancellationToken)
    {
        var sendStartTick = Stopwatch.GetTimestamp();

        if (_vrcftQueue.Count > 0)
        {
            _vrcftQueue.Add(new OscMessage(VrcftFrameReadyAddress, unchecked(Interlocked.Increment(ref _vrcftFrameSyncSequence))));
            await _vrcftModuleSendService.Send(_vrcftQueue, cancellationToken);
            _vrcftQueue.Clear();
        }

        if (_dfrQueue.Count > 0)
        {
            await _dfrSendService.Send(_dfrQueue, cancellationToken);
            _dfrQueue.Clear();
        }

        var producedTick = Interlocked.Exchange(ref _currentExpressionProducedTick, 0);
        if (producedTick > 0)
        {
            var elapsedTicks = sendStartTick - producedTick;
            RecordOscLatency(elapsedTicks);
        }

        var frameCapturedTick = Interlocked.Exchange(ref _currentEyeFrameCapturedTick, 0);
        if (frameCapturedTick > 0)
        {
            var elapsedTicks = sendStartTick - frameCapturedTick;
            RecordCameraToOscLatency(elapsedTicks);
        }

        LogOscLatency();
    }

    private void RecordOscLatency(long elapsedTicks)
    {
        Interlocked.Add(ref _oscLatencyTicks, elapsedTicks);
        Interlocked.Increment(ref _oscLatencySamples);

        while (true)
        {
            var currentMax = Volatile.Read(ref _oscLatencyMaxTicks);
            if (elapsedTicks <= currentMax)
                return;

            if (Interlocked.CompareExchange(ref _oscLatencyMaxTicks, elapsedTicks, currentMax) == currentMax)
                return;
        }
    }

    private void LogOscLatency()
    {
        var nowTick = Environment.TickCount64;
        var lastTick = Interlocked.Read(ref _lastLatencyLogTick);
        if (nowTick - lastTick < 2000)
            return;

        Interlocked.Exchange(ref _lastLatencyLogTick, nowTick);
        var ticks = Interlocked.Exchange(ref _oscLatencyTicks, 0);
        var samples = Interlocked.Exchange(ref _oscLatencySamples, 0);
        var maxTicks = Interlocked.Exchange(ref _oscLatencyMaxTicks, 0);
        var cameraTicks = Interlocked.Exchange(ref _cameraToOscLatencyTicks, 0);
        var cameraSamples = Interlocked.Exchange(ref _cameraToOscLatencySamples, 0);
        var cameraMaxTicks = Interlocked.Exchange(ref _cameraToOscLatencyMaxTicks, 0);
        if (samples <= 0 && cameraSamples <= 0)
            return;

        _logger.LogInformation(
            "ParameterSender latency: pipelineToSendMeanMs={PipelineToSendMeanMs:F3} pipelineToSendMaxMs={PipelineToSendMaxMs:F3} pipelineToSendSamples={PipelineToSendSamples} cameraToSendMeanMs={CameraToSendMeanMs:F3} cameraToSendMaxMs={CameraToSendMaxMs:F3} cameraToSendSamples={CameraToSendSamples} useDfr={UseDfr} nativeEyes={NativeEyes}",
            samples > 0 ? ticks * 1000.0 / Stopwatch.Frequency / samples : 0,
            maxTicks * 1000.0 / Stopwatch.Frequency,
            samples,
            cameraSamples > 0 ? cameraTicks * 1000.0 / Stopwatch.Frequency / cameraSamples : 0,
            cameraMaxTicks * 1000.0 / Stopwatch.Frequency,
            cameraSamples,
            _useDfr,
            _sendNativeVrcEyeTracking);
    }

    private void RecordCameraToOscLatency(long elapsedTicks)
    {
        Interlocked.Add(ref _cameraToOscLatencyTicks, elapsedTicks);
        Interlocked.Increment(ref _cameraToOscLatencySamples);

        while (true)
        {
            var currentMax = Volatile.Read(ref _cameraToOscLatencyMaxTicks);
            if (elapsedTicks <= currentMax)
                return;

            if (Interlocked.CompareExchange(ref _cameraToOscLatencyMaxTicks, elapsedTicks, currentMax) == currentMax)
                return;
        }
    }

    public override void Dispose()
    {
        _processingLoopService.ExpressionChangeEvent -= ExpressionUpdateHandler;
        _pendingExpressionsSignal.Dispose();
        base.Dispose();
    }
}
