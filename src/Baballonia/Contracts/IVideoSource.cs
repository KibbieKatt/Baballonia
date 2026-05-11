using Baballonia.Services.Inference.Enums;
using OpenCvSharp;
using System;

namespace Baballonia.Services.Inference;

public interface IVideoSource : IDisposable
{
    long FrameSequence { get; }
    long LatestFrameTimestampTick { get; }
    long LastDeliveredFrameSequence { get; }
    long LastDeliveredFrameTimestampTick { get; }
    string SourceDescription { get; }
    bool Start();
    bool Stop();
    Mat? GetFrame(ColorType? color = null);

}
