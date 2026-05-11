using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Capture = Baballonia.SDK.Capture;

namespace Baballonia.VFTCapture.Windows;

/// <summary>
/// Vive Facial Tracker camera capture
/// </summary>
public sealed class WindowsVftCapture(string source, ILogger logger) : Capture(source, logger)
{
    private readonly Mat _originalMat = new();
    private VideoCapture? _videoCapture;
    private bool _loop;

    /// <summary>
    /// Starts video capture and applies custom resolution and framerate settings.
    /// </summary>
    /// <returns>True if the video capture started successfully, otherwise false.</returns>
    public override async Task<bool> StartCapture()
    {
        Logger.LogDebug("Starting VFT camera capture...");

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
        {
            try
            {
                // Open the VFT device and initialize it.
                SetTrackerState(setActive: true);

                // Initialize VideoCapture with URL, timeout for robustness
                if (int.TryParse(Source, out var index))
                    _videoCapture = await Task.Run(() => VideoCapture.FromCamera(index, VideoCaptureAPIs.MSMF), cts.Token);
                else
                    _videoCapture = await Task.Run(() => new VideoCapture(Source), cts.Token);

                _videoCapture.ConvertRgb = false;

                _loop = true;
                _ = Task.Run(VideoCapture_UpdateLoop);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Failed to start VFT camera capture");
                IsReady = false;
                return IsReady;
            }
        }

        IsReady = _videoCapture!.IsOpened();
        Logger.LogDebug("VFT camera capture started successfully: " + IsReady);
        return IsReady;
    }

    private Task VideoCapture_UpdateLoop()
    {
        while (_loop)
        {
            try
            {
                IsReady = _videoCapture?.Read(_originalMat) == true;
                if (!IsReady) continue;

                using var yuyvMat = Mat.FromPixelData(400, 400, MatType.CV_8UC2, _originalMat.Data);
                using var grayscale = yuyvMat.CvtColor(ColorConversionCodes.YUV2GRAY_Y422, 0);
                using var cropped = grayscale.ColRange(VFTCommon.ColumnRange).Clone();
                using var resized = cropped.Resize(VFTCommon.ImageSize);
                using var blurred = resized.GaussianBlur(VFTCommon.GaussianBlurSize, 0);

                var rawMat = blurred.LUT(VFTCommon.Lut);
                SetRawMat(rawMat);
            }
            // catch (TaskCanceledException)
            // {
            //     return;
            // }
            catch (Exception)
            {
                // ignored
            }
        }

        return Task.CompletedTask;
    }

    private void SetTrackerState(bool setActive)
    {
        if (setActive)
            WindowsUsbCommunicator.activate_tracker(0);
        else
            WindowsUsbCommunicator.deactivate_tracker(0);
    }

    /// <summary>
    /// Stops video capture and cleans up resources.
    /// </summary>
    /// <returns>True if capture stopped successfully, otherwise false.</returns>
    public override Task<bool> StopCapture()
    {
        Logger.LogDebug("Stopping VFT camera capture...");

        if (_videoCapture is null)
        {
            Logger.LogDebug("VFT VideoCapture is already null, returning false");
            return Task.FromResult(false);
        }

        _loop = false;
        IsReady = false;
        _videoCapture.Release();
        _videoCapture.Dispose();
        _videoCapture = null;
        SetTrackerState(false);
        Logger.LogDebug("VFT camera capture stopped successfully");
        return Task.FromResult(true);
    }
}
