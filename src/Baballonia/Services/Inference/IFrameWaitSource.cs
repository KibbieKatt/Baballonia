using System;
using System.Threading;

namespace Baballonia.Services.Inference;

public interface IFrameWaitSource
{
    bool WaitForFrameAfter(long lastSeenSequence, TimeSpan timeout, CancellationToken cancellationToken);
}
