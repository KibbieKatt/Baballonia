using System.IO.Pipes;
using Baballonia.SDK;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace Baballonia.NamedPipeCapture;

/// <summary>
/// Captures frames from a named pipe source.
/// Pipe data is expected as a stream of raw BGR frames prefixed with an 8-byte header:
///   [4 bytes int32 width][4 bytes int32 height] followed by width*height*3 bytes of BGR data.
/// Cross-platform: works on Windows, Linux, and macOS via System.IO.Pipes.
///
/// Address format:  pipe://&lt;pipeName&gt;   (e.g. "pipe://my-camera-feed")
/// On Windows this resolves to \\.\pipe\&lt;pipeName&gt;.
/// On Linux/macOS the runtime creates a socket-backed pipe automatically.
/// </summary>
public sealed class NamedPipeCapture : Capture
{
    private readonly string _pipeName;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;

    /// <param name="pipeName">The bare pipe name without any path prefix.</param>
    /// <param name="logger">Logger instance.</param>
    public NamedPipeCapture(string pipeName, ILogger logger)
        : base($"pipe://{pipeName}", logger)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            throw new ArgumentException("Pipe name must not be empty.", nameof(pipeName));

        _pipeName = pipeName;
    }

    /// <inheritdoc/>
    public override async Task<bool> StartCapture()
    {
        if (IsReady)
        {
            Logger.LogWarning("NamedPipeCapture: already running on pipe '{PipeName}'.", _pipeName);
            return false;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // Perform the initial connection on the calling thread so the caller knows
        // whether the pipe actually exists before we hand back control.
        NamedPipeClientStream pipe;
        try
        {
            pipe = new NamedPipeClientStream(
                serverName: ".",
                pipeName: _pipeName,
                direction: PipeDirection.In,
                options: PipeOptions.Asynchronous);

            Logger.LogInformation("NamedPipeCapture: connecting to pipe '{PipeName}'…", _pipeName);
            await pipe.ConnectAsync(5_000, cancellationToken: token);
            Logger.LogInformation("NamedPipeCapture: connected to pipe '{PipeName}'.", _pipeName);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "NamedPipeCapture: failed to connect to pipe '{PipeName}'.", _pipeName);
            return false;
        }

        IsReady = true;
        _readLoop = Task.Run(() => ReadLoopAsync(pipe, token), token);
        return true;
    }

    /// <inheritdoc/>
    public override async Task<bool> StopCapture()
    {
        if (!IsReady)
            return false;

        IsReady = false;
        _cts?.Cancel();

        if (_readLoop is not null)
        {
            try { await _readLoop; }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "NamedPipeCapture: exception during read-loop shutdown.");
            }
        }

        _cts?.Dispose();
        _cts = null;
        _readLoop = null;

        Logger.LogInformation("NamedPipeCapture: stopped on pipe '{PipeName}'.", _pipeName);
        return true;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task ReadLoopAsync(NamedPipeClientStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            Logger.LogDebug("NamedPipeCapture: read loop started.");

            try
            {
                while (!ct.IsCancellationRequested && pipe.IsConnected)
                {
                    // --- Read 8-byte header (width + height) -----------------
                    int width, height;
                    try
                    {
                        width  = await ReadInt32Async(pipe, ct);
                        height = await ReadInt32Async(pipe, ct);
                    }
                    catch (EndOfStreamException)
                    {
                        Logger.LogInformation("NamedPipeCapture: pipe closed by server (EOF in header).");
                        break;
                    }

                    if (width <= 0 || height <= 0 || width > 16_384 || height > 16_384)
                    {
                        Logger.LogWarning(
                            "NamedPipeCapture: invalid frame dimensions {W}x{H} — skipping.",
                            width, height);
                        continue;
                    }

                    // --- Read BGR payload ------------------------------------
                    int frameBytes = width * height * 3;
                    byte[] buffer = new byte[frameBytes];

                    try
                    {
                        await ReadExactAsync(pipe, buffer, frameBytes, ct);
                    }
                    catch (EndOfStreamException)
                    {
                        Logger.LogInformation("NamedPipeCapture: pipe closed by server (EOF in payload).");
                        break;
                    }

                    // --- Wrap in Mat and hand off ----------------------------
                    // Mat.FromArray copies, so the byte[] can be reclaimed by GC normally.
                    var mat = Mat.FromArray(buffer).Reshape(3, rows: height);
                    SetRawMat(mat);

                    Logger.LogTrace("NamedPipeCapture: frame {W}x{H} acquired.", width, height);
                }
            }
            catch (OperationCanceledException)
            {
                Logger.LogDebug("NamedPipeCapture: read loop cancelled.");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "NamedPipeCapture: unexpected error in read loop.");
            }
            finally
            {
                IsReady = false;
                Logger.LogDebug("NamedPipeCapture: read loop exited.");
            }
        }
    }

    /// <summary>Reads exactly 4 bytes and returns them as a big-endian int32.</summary>
    private static async Task<int> ReadInt32Async(Stream stream, CancellationToken ct)
    {
        byte[] buf = new byte[4];
        await ReadExactAsync(stream, buf, 4, ct);
        if (BitConverter.IsLittleEndian) Array.Reverse(buf);
        return BitConverter.ToInt32(buf, 0);
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes into <paramref name="buffer"/>.</summary>
    private static async Task ReadExactAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer, offset, count - offset, ct);
            if (read == 0)
                throw new EndOfStreamException("Pipe closed before all bytes were received.");
            offset += read;
        }
    }

    public override void Dispose()
    {
        StopCapture().GetAwaiter().GetResult();
        base.Dispose();
    }
}
