using Baballonia.SDK;
using Baballonia.Services.Inference;
using Baballonia.Services.Inference.Enums;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Threading;

namespace Baballonia.Services.Inference.VideoSources;

public class SingleCameraSource : IVideoSource, IFrameWaitSource
{
    private ILogger _logger;
    public Size CameraSize;
    private string _cameraAddress;
    private readonly Capture _capture;
    private long _requestCount;
    private long _nullRawMatCount;
    private long _returnedFrameCount;
    private long _lastLogTick;
    private long _lastDeliveredFrameSequence;
    private long _lastDeliveredFrameTimestampTick;
    public long FrameSequence => _capture.LatestFrameSequence;
    public long LastDeliveredFrameSequence => Interlocked.Read(ref _lastDeliveredFrameSequence);
    public long LastDeliveredFrameTimestampTick => Interlocked.Read(ref _lastDeliveredFrameTimestampTick);
    public string SourceDescription => _cameraAddress;

    public SingleCameraSource(
        ILogger logger,
        Capture capture,
        string cameraAddress)
    {
        _logger = logger;
        _capture = capture;
        _cameraAddress = cameraAddress;
        CameraSize = new Size(0, 0);
    }

    public bool Start()
    {
        return _capture.StartCapture().GetAwaiter().GetResult();
    }

    public long LatestFrameTimestampTick => _capture.LatestFrameTimestampTick;

    public bool Stop()
    {
        return _capture.StopCapture().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Captures Image and transforms it to target colorspace
    /// </summary>
    /// <param name="color">colorspace to which captured image would be transformed, uses captured image colorspace by default.</param>
    /// <returns>captured image</returns>
    public Mat? GetFrame(ColorType? color = null)
    {
        Interlocked.Increment(ref _requestCount);
        var snapshot = _capture.AcquireRawFrameSnapshot();
        var rawMat = snapshot.Frame;
        if (rawMat == null)
        {
            Interlocked.Increment(ref _nullRawMatCount);
            LogFrameState("raw-null");
            return null;
        }

        CameraSize.Width = rawMat.Width;
        CameraSize.Height = rawMat.Height;

        Mat image;
        if (color == null ||
            color == (rawMat.Channels() == 1 ? ColorType.Gray8 : ColorType.Bgr24))
        {
            image = rawMat;
        }
        else
        {
            var convertedMat = new Mat();
            Cv2.CvtColor(rawMat, convertedMat,
                (rawMat.Channels() == 1)
                    ? color switch
                    {
                        ColorType.Bgr24 => ColorConversionCodes.GRAY2BGR,
                        ColorType.Rgb24 => ColorConversionCodes.GRAY2RGB,
                        ColorType.Rgba32 => ColorConversionCodes.GRAY2RGBA,
                    }
                     : color switch
                     {
                         ColorType.Gray8 => ColorConversionCodes.BGR2GRAY,
                         ColorType.Rgb24 => ColorConversionCodes.BGR2RGB,
                         ColorType.Rgba32 => ColorConversionCodes.BGR2RGBA,
                     });
            image = convertedMat;
            rawMat.Dispose();
        }

        Interlocked.Exchange(ref _lastDeliveredFrameSequence, snapshot.Sequence);
        Interlocked.Exchange(ref _lastDeliveredFrameTimestampTick, snapshot.TimestampTick);
        Interlocked.Increment(ref _returnedFrameCount);
        LogFrameState("returned", image);
        return image;
    }

    public void Dispose()
    {
        Stop();
        _capture.Dispose();
    }

    public bool WaitForFrameAfter(long lastSeenSequence, TimeSpan timeout, CancellationToken cancellationToken)
    {
        return _capture.WaitForNewFrame(lastSeenSequence, timeout, cancellationToken);
    }

    private void LogFrameState(string stage, Mat? image = null)
    {
        var nowTick = Environment.TickCount64;
        var lastTick = Interlocked.Read(ref _lastLogTick);
        if (nowTick - lastTick < 2000)
            return;

        Interlocked.Exchange(ref _lastLogTick, nowTick);
        _logger.LogInformation(
            "SingleCameraSource {Stage}: address={Address} requests={Requests} rawNull={RawNull} returned={Returned} captureReady={CaptureReady} frame={Width}x{Height}x{Channels}",
            stage,
            _cameraAddress,
            Interlocked.Read(ref _requestCount),
            Interlocked.Read(ref _nullRawMatCount),
            Interlocked.Read(ref _returnedFrameCount),
            _capture.IsReady,
            image?.Width ?? 0,
            image?.Height ?? 0,
            image?.Channels() ?? 0);
    }
}
