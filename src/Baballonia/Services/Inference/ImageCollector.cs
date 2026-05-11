using OpenCvSharp;
using System;

namespace Baballonia.Services.Inference;

public class ImageCollector : IImageTransformer, IDisposable
{
    private readonly Mat?[] _history = new Mat[4];
    private readonly Mat[] _stereoChannels = new Mat[2];
    private readonly Mat[] _octoChannels = new Mat[8];
    private int _historyHead;
    private int _historyCount;

    public Mat? Apply(Mat image)
    {
        Mat[] split = Cv2.Split(image);
        Mat? merged = null;
        try
        {
            foreach (var mat in split)
                Cv2.EqualizeHist(mat, mat);

            merged = new Mat();
            _stereoChannels[0] = split[1];
            _stereoChannels[1] = split[0];
            Cv2.Merge(_stereoChannels, merged);
        }
        finally
        {
            foreach (var mat in split)
                mat.Dispose();
        }

        PushFrame(merged);
        if (_historyCount < _history.Length)
            return null;

        try
        {
            var channelIndex = 0;
            for (var historyOffset = 0; historyOffset < _history.Length; historyOffset++)
            {
                var frameIndex = (_historyHead + _historyCount - 1 - historyOffset + _history.Length) % _history.Length;
                var frame = _history[frameIndex]!;
                var splitChannels = Cv2.Split(frame);
                _octoChannels[channelIndex++] = splitChannels[0];
                _octoChannels[channelIndex++] = splitChannels[1];
            }

            var octoMatrix = new Mat();
            Cv2.Merge(_octoChannels, octoMatrix);
            return octoMatrix;
        }
        finally
        {
            for (var i = 0; i < _octoChannels.Length; i++)
            {
                _octoChannels[i]?.Dispose();
                _octoChannels[i] = null!;
            }
        }
    }

    private void PushFrame(Mat frame)
    {
        if (_historyCount < _history.Length)
        {
            var insertIndex = (_historyHead + _historyCount) % _history.Length;
            _history[insertIndex] = frame;
            _historyCount++;
            return;
        }

        _history[_historyHead]?.Dispose();
        _history[_historyHead] = frame;
        _historyHead = (_historyHead + 1) % _history.Length;
    }

    public void Dispose()
    {
        for (var i = 0; i < _history.Length; i++)
        {
            _history[i]?.Dispose();
            _history[i] = null;
        }
    }
}
