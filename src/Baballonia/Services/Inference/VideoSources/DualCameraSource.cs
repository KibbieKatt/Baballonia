using Baballonia.Services.Inference.Enums;
using OpenCvSharp;
using System;

namespace Baballonia.Services.Inference;

public class DualCameraSource : IVideoSource
{
    public IVideoSource? LeftCam;
    public IVideoSource? RightCam;
    public long LastDeliveredFrameSequence
    {
        get
        {
            var left = LeftCam?.LastDeliveredFrameSequence ?? 0;
            var right = RightCam?.LastDeliveredFrameSequence ?? 0;
            return unchecked((left * 397) ^ right);
        }
    }

    public long LastDeliveredFrameTimestampTick
    {
        get
        {
            var left = LeftCam?.LastDeliveredFrameTimestampTick ?? 0;
            var right = RightCam?.LastDeliveredFrameTimestampTick ?? 0;
            return Math.Max(left, right);
        }
    }

    public string SourceDescription => $"{LeftCam?.SourceDescription ?? "null"}|{RightCam?.SourceDescription ?? "null"}";

    public long FrameSequence
    {
        get
        {
            var left = LeftCam?.FrameSequence ?? 0;
            var right = RightCam?.FrameSequence ?? 0;
            return unchecked((left * 397) ^ right);
        }
    }

    public long LatestFrameTimestampTick
    {
        get
        {
            var left = LeftCam?.LatestFrameTimestampTick ?? 0;
            var right = RightCam?.LatestFrameTimestampTick ?? 0;
            return Math.Max(left, right);
        }
    }

    private Mat? LastLeftImage;
    private Mat? LastRightImage;

    public bool Start()
    {
        return (LeftCam?.Start() ?? true) && (RightCam?.Start() ?? true);
    }

    public bool Stop()
    {
        return (LeftCam?.Stop() ?? true) && (RightCam?.Stop() ?? true);
    }

    // Here we try to acquire 2 images from both cameras and stitch them into a single image
    // if at least one image can be acquired, try to stitch it with last second image
    public Mat? GetFrame(ColorType? color = null)
    {
        var leftImage = LeftCam?.GetFrame(color);
        var rightImage = RightCam?.GetFrame(color);

        if (leftImage == null && rightImage == null)
            return null;

        // Track which images are new vs fallback
        var leftIsNew = leftImage != null;
        var rightIsNew = rightImage != null;

        Mat? clonedLeft = null;
        Mat? clonedRight = null;
        try
        {
            var effectiveLeft = leftImage ?? LastLeftImage;
            var effectiveRight = rightImage ?? LastRightImage;

            switch (effectiveLeft)
            {
                case null when effectiveRight == null:
                    return null;
                case null:
                    clonedLeft = effectiveRight!.Clone();
                    effectiveLeft = clonedLeft;
                    break;
                default:
                    if (effectiveRight == null)
                    {
                        clonedRight = effectiveLeft.Clone();
                        effectiveRight = clonedRight;
                    }
                    break;
            }

            int minHeight = Math.Min(effectiveLeft.Rows, effectiveRight.Rows);
            int minWidth = Math.Min(effectiveLeft.Cols, effectiveRight.Cols);

            using var resizedLeft = new Mat();
            using var resizedRight = new Mat();
            Cv2.Resize(effectiveLeft, resizedLeft, new Size(minWidth, minHeight));
            Cv2.Resize(effectiveRight, resizedRight, new Size(minWidth, minHeight));

            int height = Math.Max(resizedRight.Rows, resizedLeft.Rows);
            int width = resizedRight.Cols + resizedLeft.Cols;

            Mat result = new Mat(height, width, resizedLeft.Type(), Scalar.All(0));

            resizedLeft.CopyTo(result[new Rect(0, 0, resizedLeft.Cols, resizedRight.Rows)]);
            resizedRight.CopyTo(result[new Rect(resizedLeft.Cols, 0, resizedRight.Cols, resizedRight.Rows)]);

            if (leftIsNew)
            {
                LastLeftImage?.Dispose();
                LastLeftImage = resizedLeft.Clone();
            }

            if (rightIsNew)
            {
                LastRightImage?.Dispose();
                LastRightImage = resizedRight.Clone();
            }

            return result;
        }
        finally
        {
            clonedLeft?.Dispose();
            clonedRight?.Dispose();

            if (leftIsNew)
                leftImage?.Dispose();

            if (rightIsNew)
                rightImage?.Dispose();
        }
    }

    public void Dispose()
    {
        LeftCam?.Dispose();
        RightCam?.Dispose();
        LastLeftImage?.Dispose();
        LastRightImage?.Dispose();
    }
}
