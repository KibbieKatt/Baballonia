using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Capture = Baballonia.SDK.Capture;

namespace Baballonia.OpenCVCapture;

/// <summary>
/// Wrapper class for OpenCV
/// </summary>
public sealed class OpenCvCapture : Capture
{
    private const string MsmfPrefix = "msmf:";
    private const string DshowPrefix = "dshow:";
    private readonly object _latestFrameLock = new();
    private VideoCapture? _videoCapture;
    private Mat? _latestFrame;
    private static readonly VideoCaptureAPIs PreferredBackend;
    private readonly VideoCaptureAPIs _selectedBackend;
    private readonly string _normalizedSource;
    private Task? _updateTask;
    private CancellationTokenSource? _updateTaskCts;
    private long _readSuccessCount;
    private long _readFailureCount;
    private long _acquireCount;
    private long _acquireNullCount;
    private long _lastReadLogTick;
    private long _latestFrameSequence;
    private long _latestFrameTimestampTick;
    private readonly TimeSpan _targetFrameInterval;

    public override long LatestFrameSequence => Interlocked.Read(ref _latestFrameSequence);
    public override long LatestFrameTimestampTick => Interlocked.Read(ref _latestFrameTimestampTick);

    public OpenCvCapture(string source, ILogger<OpenCvCapture> logger) : base(source, logger)
    {
        _selectedBackend = ResolveBackend(source, out _normalizedSource);
        _targetFrameInterval = ResolveTargetFrameInterval(source);
    }

    static OpenCvCapture()
    {
        if (OperatingSystem.IsWindows())
        {
            PreferredBackend = VideoCaptureAPIs.DSHOW;
        }
        else if (OperatingSystem.IsLinux())
        {
            PreferredBackend = VideoCaptureAPIs.GSTREAMER;
        }
        else if (OperatingSystem.IsMacOS())
        {
            PreferredBackend = VideoCaptureAPIs.AVFOUNDATION;
        }
        else
        {
            PreferredBackend = VideoCaptureAPIs.ANY;
        }
    }

    public override async Task<bool> StartCapture()
    {
        await StopCapture();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            if (int.TryParse(_normalizedSource, out var index))
                _videoCapture = await Task.Run(() => VideoCapture.FromCamera(index, _selectedBackend), cts.Token);
            else
                _videoCapture = await Task.Run(() => new VideoCapture(_normalizedSource, _selectedBackend), cts.Token);
        }
        catch (Exception e)
        {
            Logger.LogError(e, "OpenCV capture start failed for {Source}", _normalizedSource);
            IsReady = false;
            return false;
        }

        _videoCapture.ConvertRgb = true;
        try
        {
            _videoCapture.Set(VideoCaptureProperties.BufferSize, 1);
        }
        catch
        {
        }
        if (_selectedBackend == VideoCaptureAPIs.MSMF)
        {
            try
            {
                _videoCapture.Set(VideoCaptureProperties.Fps, 90);
            }
            catch
            {
            }
        }
        IsReady = _videoCapture.IsOpened();

        _updateTaskCts?.Dispose();
        _updateTaskCts = new CancellationTokenSource();
        var token = _updateTaskCts.Token;
        _updateTask = Task.Factory.StartNew(() => VideoCapture_UpdateLoop(_videoCapture, token), token,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

        LogReadState("start");
        return IsReady;
    }

    public override Mat? AcquireRawMat()
    {
        return AcquireRawFrameSnapshot().Frame;
    }

    public override RawFrameSnapshot AcquireRawFrameSnapshot()
    {
        Interlocked.Increment(ref _acquireCount);
        lock (_latestFrameLock)
        {
            if (_latestFrame == null || _latestFrame.Empty())
            {
                Interlocked.Increment(ref _acquireNullCount);
                LogReadState("acquire-null");
                return new RawFrameSnapshot(null, 0, 0, Source);
            }

            LogReadState("acquire");
            return new RawFrameSnapshot(
                _latestFrame.Clone(),
                Interlocked.Read(ref _latestFrameSequence),
                Interlocked.Read(ref _latestFrameTimestampTick),
                Source);
        }
    }

    private Task VideoCapture_UpdateLoop(VideoCapture capture, CancellationToken ct)
    {
        TryElevateCaptureThreadPriority();
        while (!ct.IsCancellationRequested)
        {
            var readStart = Stopwatch.GetTimestamp();
            var frame = new Mat();
            try
            {
                IsReady = capture.Read(frame);
                if (IsReady && !frame.Empty())
                {
                    Interlocked.Increment(ref _readSuccessCount);
                    lock (_latestFrameLock)
                    {
                        _latestFrame?.Dispose();
                        _latestFrame = frame.Clone();
                        Interlocked.Increment(ref _latestFrameSequence);
                        Interlocked.Exchange(ref _latestFrameTimestampTick, Stopwatch.GetTimestamp());
                        SignalFrameReady();
                    }
                    frame.Dispose();
                    LogReadState("read");
                }
                else
                {
                    Interlocked.Increment(ref _readFailureCount);
                    frame.Dispose();
                    LogReadState("read-fail");
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                Interlocked.Increment(ref _readFailureCount);
                frame.Dispose();
                IsReady = false;
                Logger.LogError(e, "OpenCV read failed for {Source}", Source);
                LogReadState("read-exception");
            }

            PaceLoop(readStart, ct);
        }

        return Task.CompletedTask;
    }

    private static void TryElevateCaptureThreadPriority()
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

    private void LogReadState(string stage)
    {
        var nowTick = Environment.TickCount64;
        var lastTick = Interlocked.Read(ref _lastReadLogTick);
        if (nowTick - lastTick < 2000)
            return;

        Interlocked.Exchange(ref _lastReadLogTick, nowTick);
        Logger.LogInformation(
            "OpenCvCapture {Stage}: source={Source} backend={Backend} success={Success} failure={Failure} acquire={Acquire} acquireNull={AcquireNull} ready={Ready}",
            stage,
            Source,
            _selectedBackend,
            Interlocked.Read(ref _readSuccessCount),
            Interlocked.Read(ref _readFailureCount),
            Interlocked.Read(ref _acquireCount),
            Interlocked.Read(ref _acquireNullCount),
            IsReady);
    }

    public override Task<bool> StopCapture()
    {
        if (_videoCapture is null)
        {
            IsReady = false;
            LogReadState("stop");
            return Task.FromResult(false);
        }

        if (_updateTask != null)
        {
            _updateTaskCts?.Cancel();
            try
            {
                _updateTask.Wait();
            }
            catch (AggregateException e) when (e.InnerExceptions.All(ex => ex is TaskCanceledException or OperationCanceledException))
            {
            }
            _updateTask = null;
        }

        _updateTaskCts?.Dispose();
        _updateTaskCts = null;

        lock (_latestFrameLock)
        {
            _latestFrame?.Dispose();
            _latestFrame = null;
        }

        IsReady = false;
        _videoCapture.Release();
        _videoCapture.Dispose();
        _videoCapture = null;
        LogReadState("stop");
        return Task.FromResult(true);
    }

    private void PaceLoop(long readStart, CancellationToken ct)
    {
        if (_targetFrameInterval <= TimeSpan.Zero)
            return;

        var deadline = readStart + (long)(_targetFrameInterval.TotalSeconds * Stopwatch.Frequency);
        while (!ct.IsCancellationRequested)
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

    private static VideoCaptureAPIs ResolveBackend(string source, out string normalizedSource)
    {
        normalizedSource = source;
        if (OperatingSystem.IsWindows())
        {
            if (source.StartsWith(MsmfPrefix, StringComparison.OrdinalIgnoreCase))
            {
                normalizedSource = source[MsmfPrefix.Length..];
                return VideoCaptureAPIs.MSMF;
            }

            if (source.StartsWith(DshowPrefix, StringComparison.OrdinalIgnoreCase))
            {
                normalizedSource = source[DshowPrefix.Length..];
                return VideoCaptureAPIs.DSHOW;
            }
        }

        return PreferredBackend;
    }

    private static TimeSpan ResolveTargetFrameInterval(string source)
    {
        if (!OperatingSystem.IsWindows())
            return TimeSpan.Zero;

        if (source.StartsWith(MsmfPrefix, StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromMilliseconds(1000.0 / 90.0);

        return TimeSpan.Zero;
    }
}
