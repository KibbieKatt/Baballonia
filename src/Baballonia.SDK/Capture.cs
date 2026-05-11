using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;

namespace Baballonia.SDK;

/// <summary>
/// Defines custom camera stream behavior
/// </summary>
public abstract class Capture(string source, ILogger logger) : IDisposable
{
    public readonly record struct RawFrameSnapshot(Mat? Frame, long Sequence, long TimestampTick, string Source);

    protected ILogger Logger = logger;
    private Mat? _rawMat;
    private readonly object _rawMatLock = new();
    private readonly AutoResetEvent _frameReadyEvent = new(false);

    /// <summary>
    /// Where this Capture source is currently pulling data from
    /// </summary>
    public string Source { get; set; } = source;
    public virtual long LatestFrameSequence => 0;
    public virtual long LatestFrameTimestampTick => 0;

    /// <summary>
    /// Represents the incoming frame data for this capture source.
    /// Will be `dimension` in BGR color space. <br/>
    /// Acquiring this value the caller takes ownership of the Mat object and sets the internal reference to null. <br/>
    /// Thread safe
    /// </summary>
    public virtual Mat? AcquireRawMat()
    {
        Mat? result;
        lock (_rawMatLock)
        {
            result = _rawMat;
            _rawMat = null;
        }
        return result;
    }

    public virtual RawFrameSnapshot AcquireRawFrameSnapshot()
    {
        return new RawFrameSnapshot(AcquireRawMat(), LatestFrameSequence, LatestFrameTimestampTick, Source);
    }

    /// <summary>
    /// Sets current Mat object that can be acquired by someone else. <br/>
    /// The caller gives up the responsibility for the object <br/>
    /// It is prohibited to use the value object after calling this method <br/>
    /// Thread safe
    /// </summary>
    /// <param name="value">value</param>
    protected void SetRawMat(Mat value)
    {
        lock (_rawMatLock)
        {
            if (ReferenceEquals(_rawMat, value)) return;

            _rawMat?.Dispose();
            _rawMat = value;
        }
    }

    protected void SignalFrameReady()
    {
        _frameReadyEvent.Set();
    }

    public virtual bool WaitForNewFrame(long lastSeenSequence, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (LatestFrameSequence > lastSeenSequence)
            return true;

        if (timeout <= TimeSpan.Zero)
            return false;

        var timeoutDeadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        var waitHandles = new[] { _frameReadyEvent, cancellationToken.WaitHandle };

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (LatestFrameSequence > lastSeenSequence)
                return true;

            var remainingTicks = timeoutDeadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
                return LatestFrameSequence > lastSeenSequence;

            var remainingMs = (int)Math.Ceiling(remainingTicks * 1000.0 / Stopwatch.Frequency);
            var waitResult = WaitHandle.WaitAny(waitHandles, Math.Max(1, remainingMs));
            if (waitResult == WaitHandle.WaitTimeout)
                return LatestFrameSequence > lastSeenSequence;
        }
    }
    /// <summary>
    /// Is this Capture source ready to produce data?
    /// </summary>
    public bool IsReady { get; protected set; } = false;

    /// <summary>
    /// Start Capture on this source
    /// </summary>
    /// <returns></returns>
    public abstract Task<bool> StartCapture();

    /// <summary>
    /// Stop Capture on this source
    /// </summary>
    /// <returns></returns>
    public abstract Task<bool> StopCapture();

    public virtual void Dispose()
    {
        lock (_rawMatLock)
        {
            _rawMat?.Dispose();
            _rawMat = null;
        }

        _frameReadyEvent.Dispose();
    }
}
