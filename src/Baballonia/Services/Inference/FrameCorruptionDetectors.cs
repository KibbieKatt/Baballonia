using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Baballonia.Services.Inference;

public interface IFrameCorruptionDetector : IDisposable
{
    string Mode { get; }
    (bool isCorrupted, double score, double threshold) IsCorrupted(Mat frame);
}

public static class FrameCorruptionDetectorFactory
{
    public static IFrameCorruptionDetector Create(string? mode)
    {
        return CorruptionDetectorModes.Normalize(mode) switch
        {
            var value when value == CorruptionDetectorModes.HistogramFlash => new SplitFrameCorruptionDetector(
                CorruptionDetectorModes.HistogramFlash,
                () => new HistogramFlashEyeCorruptionDetector()),
            var value when value == CorruptionDetectorModes.HybridRuntimeV2 => new SplitFrameCorruptionDetector(
                CorruptionDetectorModes.HybridRuntimeV2,
                () => new HybridRuntimeV2EyeCorruptionDetector()),
            _ => new SplitFrameCorruptionDetector(
                CorruptionDetectorModes.StockRow,
                () => new StockRowEyeCorruptionDetector())
        };
    }
}

internal sealed class SplitFrameCorruptionDetector : IFrameCorruptionDetector
{
    private readonly IEyeFrameCorruptionDetector _leftDetector;
    private readonly IEyeFrameCorruptionDetector _rightDetector;

    public SplitFrameCorruptionDetector(string mode, Func<IEyeFrameCorruptionDetector> detectorFactory)
    {
        Mode = mode;
        _leftDetector = detectorFactory();
        _rightDetector = detectorFactory();
    }

    public string Mode { get; }

    public (bool isCorrupted, double score, double threshold) IsCorrupted(Mat frame)
    {
        using var leftContext = EyeCorruptionFrameContext.Create(frame, left: true);
        using var rightContext = EyeCorruptionFrameContext.Create(frame, left: false);

        var left = _leftDetector.Evaluate(leftContext);
        var right = _rightDetector.Evaluate(rightContext);

        return (
            left.IsCorrupted || right.IsCorrupted,
            Math.Max(left.Score, right.Score),
            Math.Max(left.Threshold, right.Threshold));
    }

    public void Dispose()
    {
        _leftDetector.Dispose();
        _rightDetector.Dispose();
    }
}

internal interface IEyeFrameCorruptionDetector : IDisposable
{
    CorruptionDetectionResult Evaluate(EyeCorruptionFrameContext frame);
}

internal sealed record CorruptionDetectionResult(bool IsCorrupted, double Score, double Threshold);

internal sealed class StockRowEyeCorruptionDetector : IEyeFrameCorruptionDetector
{
    private readonly FastCorruptionDetector.FastCorruptionDetector _detector = new();

    public CorruptionDetectionResult Evaluate(EyeCorruptionFrameContext frame)
    {
        var (isCorrupted, score, threshold) = _detector.IsCorrupted(frame.Gray);
        return new CorruptionDetectionResult(isCorrupted, score, threshold);
    }

    public void Dispose()
    {
    }
}

internal sealed class HistogramFlashEyeCorruptionDetector : IEyeFrameCorruptionDetector
{
    private readonly Queue<double> _recentScores = new();
    private double[]? _previousHistogram;
    private double _previousMean;
    private double _threshold = 0.18;
    private int _frameCount;

    public CorruptionDetectionResult Evaluate(EyeCorruptionFrameContext frame)
    {
        _frameCount++;

        var histogram = DetectorMath.ComputeNormalizedHistogram(frame.Downsampled);
        var histogramDistance = _previousHistogram == null ? 0 : DetectorMath.ComputeHistogramL1(histogram, _previousHistogram);
        var meanJump = _frameCount == 1 ? 0 : Math.Abs(frame.Mean - _previousMean) / 255.0;
        var score = histogramDistance + meanJump * 0.35 + Math.Max(frame.ClippedHighRatio, frame.ClippedLowRatio) * 0.5;

        DetectorMath.UpdateMadThreshold(_recentScores, score, ref _threshold, multiplier: 8.0, minimum: 0.08);

        var warmedUp = _frameCount > 15;
        var isCorrupted = warmedUp && score > _threshold &&
                          (meanJump > 0.05 || histogramDistance > _threshold || frame.ClippedHighRatio > 0.02 || frame.ClippedLowRatio > 0.02);

        _previousHistogram = histogram;
        _previousMean = frame.Mean;

        return new CorruptionDetectionResult(isCorrupted, score, _threshold);
    }

    public void Dispose()
    {
    }
}

internal sealed class HybridRuntimeV2EyeCorruptionDetector : IEyeFrameCorruptionDetector
{
    private readonly Queue<double> _recentRowScores = new();
    private readonly Queue<double> _recentTemporalScores = new();
    private readonly Queue<double> _recentBoundaryScores = new();
    private readonly Queue<double> _recentHistogramScores = new();
    private readonly Queue<double> _recentEdgeScores = new();
    private readonly Mat _edgeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
    private Mat? _previousDownsampled;
    private Mat? _previousEdges;
    private double[]? _previousHistogram;
    private double _previousMean;
    private double _rowThreshold = 0.02;
    private double _temporalThreshold = 0.14;
    private double _boundaryThreshold = 0.18;
    private double _histogramThreshold = 0.10;
    private double _edgeThreshold = 0.18;
    private int _frameCount;

    public CorruptionDetectionResult Evaluate(EyeCorruptionFrameContext frame)
    {
        _frameCount++;

        var rowScore = DetectorMath.ComputeBidirectionalStripeScore(frame.Downsampled);
        var boundaryScore = DetectorMath.ComputeBoundaryArtifactScore(frame.Downsampled);

        var histogram = DetectorMath.ComputeNormalizedHistogram(frame.Downsampled);
        var histogramDistance = _previousHistogram == null ? 0 : DetectorMath.ComputeHistogramL1(histogram, _previousHistogram);
        var meanJump = _frameCount == 1 ? 0 : Math.Abs(frame.Mean - _previousMean) / 255.0;
        var histogramScore = histogramDistance + meanJump * 0.35 + Math.Max(frame.ClippedHighRatio, frame.ClippedLowRatio) * 0.5;

        double temporalScore = 0;
        if (_previousDownsampled != null)
        {
            using var diff = new Mat();
            Cv2.Absdiff(frame.Downsampled, _previousDownsampled, diff);
            temporalScore = Cv2.Mean(diff).Val0 / 255.0;
        }

        using var edges = new Mat();
        Cv2.Canny(frame.Downsampled, edges, 40, 120);
        double enteringRatio = 0;
        double exitingRatio = 0;
        if (_previousEdges != null)
        {
            using var dilatedPrevious = new Mat();
            using var dilatedCurrent = new Mat();
            using var invertedPrevious = new Mat();
            using var invertedCurrent = new Mat();
            using var entering = new Mat();
            using var exiting = new Mat();

            Cv2.Dilate(_previousEdges, dilatedPrevious, _edgeKernel);
            Cv2.Dilate(edges, dilatedCurrent, _edgeKernel);
            Cv2.BitwiseNot(dilatedPrevious, invertedPrevious);
            Cv2.BitwiseNot(dilatedCurrent, invertedCurrent);
            Cv2.BitwiseAnd(edges, invertedPrevious, entering);
            Cv2.BitwiseAnd(_previousEdges, invertedCurrent, exiting);

            var currentCount = Math.Max(1, Cv2.CountNonZero(edges));
            var previousCount = Math.Max(1, Cv2.CountNonZero(_previousEdges));
            enteringRatio = Cv2.CountNonZero(entering) / (double)currentCount;
            exitingRatio = Cv2.CountNonZero(exiting) / (double)previousCount;
        }

        var edgeScore = Math.Max(enteringRatio, exitingRatio);
        var saturationScore = Math.Max(frame.ClippedHighRatio, frame.ClippedLowRatio) * 2.0;
        if (frame.StdDev < 8.0)
            saturationScore += (8.0 - frame.StdDev) / 8.0;
        if (frame.Mean > 245.0 || frame.Mean < 10.0)
            saturationScore += 0.5;

        DetectorMath.UpdateMadThreshold(_recentRowScores, rowScore, ref _rowThreshold, multiplier: 7.0, minimum: 0.01);
        DetectorMath.UpdateMadThreshold(_recentTemporalScores, temporalScore, ref _temporalThreshold, multiplier: 8.0, minimum: 0.08);
        DetectorMath.UpdateMadThreshold(_recentBoundaryScores, boundaryScore, ref _boundaryThreshold, multiplier: 8.0, minimum: 0.12);
        DetectorMath.UpdateMadThreshold(_recentHistogramScores, histogramScore, ref _histogramThreshold, multiplier: 8.0, minimum: 0.08);
        DetectorMath.UpdateMadThreshold(_recentEdgeScores, edgeScore, ref _edgeThreshold, multiplier: 8.0, minimum: 0.12);

        var warmedUp = _frameCount > 45;
        var isCorrupted = warmedUp && (
            saturationScore > 0.8 ||
            (histogramScore > _histogramThreshold && temporalScore > _temporalThreshold * 0.8) ||
            (edgeScore > _edgeThreshold && (histogramScore > _histogramThreshold * 0.75 || saturationScore > 0.2)) ||
            (boundaryScore > _boundaryThreshold && temporalScore > _temporalThreshold * 0.6) ||
            (rowScore > _rowThreshold && temporalScore > _temporalThreshold * 0.8));

        var normalizedComposite = Math.Max(
            saturationScore / 0.8,
            Math.Max(
                histogramScore / Math.Max(_histogramThreshold, 1e-6),
                Math.Max(
                    edgeScore / Math.Max(_edgeThreshold, 1e-6),
                    Math.Max(
                        boundaryScore / Math.Max(_boundaryThreshold, 1e-6),
                        Math.Max(
                            rowScore / Math.Max(_rowThreshold, 1e-6),
                            temporalScore / Math.Max(_temporalThreshold, 1e-6))))));

        _previousDownsampled?.Dispose();
        _previousDownsampled = frame.Downsampled.Clone();
        _previousEdges?.Dispose();
        _previousEdges = edges.Clone();
        _previousHistogram = histogram;
        _previousMean = frame.Mean;

        return new CorruptionDetectionResult(isCorrupted, normalizedComposite, 1.0);
    }

    public void Dispose()
    {
        _previousDownsampled?.Dispose();
        _previousEdges?.Dispose();
        _edgeKernel.Dispose();
    }
}

internal sealed class EyeCorruptionFrameContext : IDisposable
{
    private EyeCorruptionFrameContext(Mat gray, Mat downsampled, double mean, double stdDev, double clippedLowRatio, double clippedHighRatio)
    {
        Gray = gray;
        Downsampled = downsampled;
        Mean = mean;
        StdDev = stdDev;
        ClippedLowRatio = clippedLowRatio;
        ClippedHighRatio = clippedHighRatio;
    }

    public Mat Gray { get; }
    public Mat Downsampled { get; }
    public double Mean { get; }
    public double StdDev { get; }
    public double ClippedLowRatio { get; }
    public double ClippedHighRatio { get; }

    public static EyeCorruptionFrameContext Create(Mat frame, bool left)
    {
        var halfWidth = Math.Max(1, frame.Width / 2);
        var width = Math.Min(halfWidth, frame.Width);
        var x = left ? 0 : Math.Max(0, frame.Width - width);

        using var roi = new Mat(frame, new Rect(x, 0, width, frame.Height));

        var gray = new Mat();
        if (roi.Channels() == 1)
            roi.CopyTo(gray);
        else
            Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);

        var downsampled = new Mat();
        Cv2.Resize(gray, downsampled, new Size(64, 64), interpolation: InterpolationFlags.Area);

        Cv2.MeanStdDev(downsampled, out var mean, out var stddev);
        var pixelCount = downsampled.Rows * downsampled.Cols;
        var clippedLow = CountWithinThreshold(downsampled, 0, 3) / (double)pixelCount;
        var clippedHigh = CountWithinThreshold(downsampled, 252, 255) / (double)pixelCount;

        return new EyeCorruptionFrameContext(gray, downsampled, mean.Val0, stddev.Val0, clippedLow, clippedHigh);
    }

    public void Dispose()
    {
        Downsampled.Dispose();
        Gray.Dispose();
    }

    private static int CountWithinThreshold(Mat mat, byte min, byte max)
    {
        var count = 0;
        for (var y = 0; y < mat.Rows; y++)
        {
            for (var x = 0; x < mat.Cols; x++)
            {
                var value = mat.Get<byte>(y, x);
                if (value >= min && value <= max)
                    count++;
            }
        }

        return count;
    }
}

internal static class DetectorMath
{
    public static double ComputeBidirectionalStripeScore(Mat frame)
    {
        var rowScore = ComputeSignalSecondDerivativeScore(GetRowMeans(frame));
        var columnScore = ComputeSignalSecondDerivativeScore(GetColumnMeans(frame));
        return Math.Max(rowScore, columnScore);
    }

    public static double[] GetRowMeans(Mat frame)
    {
        var means = new double[frame.Rows];
        for (var y = 0; y < frame.Rows; y++)
        {
            double sum = 0;
            for (var x = 0; x < frame.Cols; x++)
                sum += frame.Get<byte>(y, x);

            means[y] = sum / frame.Cols / 255.0;
        }

        return means;
    }

    public static double[] GetColumnMeans(Mat frame)
    {
        var means = new double[frame.Cols];
        for (var x = 0; x < frame.Cols; x++)
        {
            double sum = 0;
            for (var y = 0; y < frame.Rows; y++)
                sum += frame.Get<byte>(y, x);

            means[x] = sum / frame.Rows / 255.0;
        }

        return means;
    }

    public static double ComputeSignalSecondDerivativeScore(double[] signal)
    {
        if (signal.Length < 3)
            return 0;

        var secondDiffs = new double[signal.Length - 2];
        for (var i = 0; i < secondDiffs.Length; i++)
            secondDiffs[i] = Math.Abs(signal[i + 2] - 2 * signal[i + 1] + signal[i]);

        Array.Sort(secondDiffs);
        var index = Math.Min(secondDiffs.Length - 1, (int)Math.Floor(secondDiffs.Length * 0.95));
        return secondDiffs[index];
    }

    public static void UpdateMadThreshold(Queue<double> queue, double score, ref double threshold, double multiplier, double minimum, int window = 180)
    {
        queue.Enqueue(score);
        while (queue.Count > window)
            queue.Dequeue();

        if (queue.Count < 30)
            return;

        var values = queue.OrderBy(v => v).ToArray();
        var median = values[values.Length / 2];
        var deviations = values.Select(v => Math.Abs(v - median)).OrderBy(v => v).ToArray();
        var mad = deviations[deviations.Length / 2];
        threshold = Math.Max(minimum, median + multiplier * mad);
    }

    public static double ComputeBoundaryArtifactScore(Mat frame, int blockSize = 8)
    {
        var horizontalBoundary = 0.0;
        var horizontalInterior = 0.0;
        var verticalBoundary = 0.0;
        var verticalInterior = 0.0;
        var horizontalBoundaryCount = 0;
        var horizontalInteriorCount = 0;
        var verticalBoundaryCount = 0;
        var verticalInteriorCount = 0;

        for (var y = 0; y < frame.Rows; y++)
        {
            for (var x = 0; x < frame.Cols - 1; x++)
            {
                var diff = Math.Abs(frame.Get<byte>(y, x + 1) - frame.Get<byte>(y, x)) / 255.0;
                if ((x + 1) % blockSize == 0)
                {
                    horizontalBoundary += diff;
                    horizontalBoundaryCount++;
                }
                else
                {
                    horizontalInterior += diff;
                    horizontalInteriorCount++;
                }
            }
        }

        for (var y = 0; y < frame.Rows - 1; y++)
        {
            for (var x = 0; x < frame.Cols; x++)
            {
                var diff = Math.Abs(frame.Get<byte>(y + 1, x) - frame.Get<byte>(y, x)) / 255.0;
                if ((y + 1) % blockSize == 0)
                {
                    verticalBoundary += diff;
                    verticalBoundaryCount++;
                }
                else
                {
                    verticalInterior += diff;
                    verticalInteriorCount++;
                }
            }
        }

        var horizontalRatio = (horizontalBoundary / Math.Max(1, horizontalBoundaryCount)) /
                              Math.Max(1e-6, horizontalInterior / Math.Max(1, horizontalInteriorCount));
        var verticalRatio = (verticalBoundary / Math.Max(1, verticalBoundaryCount)) /
                            Math.Max(1e-6, verticalInterior / Math.Max(1, verticalInteriorCount));
        return Math.Max(horizontalRatio, verticalRatio) - 1.0;
    }

    public static double[] ComputeNormalizedHistogram(Mat frame, int bins = 32)
    {
        var histogram = new double[bins];
        var total = frame.Rows * frame.Cols;
        if (total == 0)
            return histogram;

        for (var y = 0; y < frame.Rows; y++)
        {
            for (var x = 0; x < frame.Cols; x++)
            {
                var value = frame.Get<byte>(y, x);
                var index = Math.Min(bins - 1, value * bins / 256);
                histogram[index]++;
            }
        }

        for (var i = 0; i < histogram.Length; i++)
            histogram[i] /= total;

        return histogram;
    }

    public static double ComputeHistogramL1(double[] current, double[] previous)
    {
        var sum = 0.0;
        var count = Math.Min(current.Length, previous.Length);
        for (var i = 0; i < count; i++)
            sum += Math.Abs(current[i] - previous[i]);

        return sum * 0.5;
    }
}
