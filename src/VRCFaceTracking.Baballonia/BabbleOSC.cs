using System.Diagnostics;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Sockets;
using VRCFaceTracking.Core.OSC;
using VRCFaceTracking.Core.Params.Data;
using VRCFaceTracking.Core.Params.Expressions;

namespace VRCFaceTracking.Baballonia;

public class BabbleOsc
{
    private const string FrameReadyAddress = "/vrcft/babble/frameReady";
    public static readonly float[] EyeExpressions = new float[12];
    private static readonly TimeSpan RebindBackoff = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan StaleDataThreshold = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FrameReadyTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan BindRetryWindow = TimeSpan.FromSeconds(2);

    private Socket? _receiver;
    private readonly object _receiverLock = new();
    private readonly ILogger _logger;

    private bool _loop = true;

    private readonly Thread? _thread;

    private readonly int _resolvedPort;

    private readonly string? _resolvedHost;
    private readonly Action? _onDataUpdated;

    private const string DefaultHost = "127.0.0.1";

    private const int DefaultPort = 8888;

    private const int TimeoutMs = 50;
    private int _consecutiveReceiveErrors;
    private long _lastPacketTimestamp;
    private int _staleStateActive;
    private int _receivedPacketCount;
    private long _lastReceiveLogTimestamp;
    private int _frameSyncActive;
    private int _pendingImmediateUpdate;
    private int _frameReadyCount;
    private long _lastFrameReadyLogTimestamp;
    private long _lastFrameReadyTimestamp;
    private int _consecutiveStaleTimeouts;

    public BabbleOsc(ILogger iLogger, string host, int? port, Action? onDataUpdated = null)
    {
        if (_receiver != null)
        {
            iLogger.LogError("BabbleEyeOSC connection already exists.");
            return;
        }
        _resolvedHost = host ?? DefaultHost;
        _resolvedPort = port ?? DefaultPort;
        _onDataUpdated = onDataUpdated;
        _logger = iLogger;
        _lastPacketTimestamp = Stopwatch.GetTimestamp();
        _lastReceiveLogTimestamp = Stopwatch.GetTimestamp();
        _lastFrameReadyLogTimestamp = Stopwatch.GetTimestamp();
        _lastFrameReadyTimestamp = Stopwatch.GetTimestamp();

        iLogger.LogInformation($"Started BabbleEyeOSC with Host: {_resolvedHost} and Port {_resolvedPort}");
        RecreateReceiver();
        _loop = true;
        _thread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "BabbleOSC Listen",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    private void RecreateReceiver()
    {
        lock (_receiverLock)
        {
            try
            {
                _receiver?.Close();
                _receiver?.Dispose();
            }
            catch
            {
                // Best-effort cleanup only.
            }

            IPAddress address = IPAddress.Parse(_resolvedHost!);
            IPEndPoint localEp = new IPEndPoint(address, _resolvedPort);
            var bindStartedAt = Stopwatch.GetTimestamp();

            while (true)
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                try
                {
                    socket.Bind(localEp);
                    socket.ReceiveTimeout = TimeoutMs;
                    _receiver = socket;
                    return;
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse &&
                                                 Stopwatch.GetElapsedTime(bindStartedAt) < BindRetryWindow)
                {
                    socket.Dispose();
                    Thread.Sleep(RebindBackoff);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        }
    }

    private void ListenLoop()
    {
        byte[] array = new byte[4096];
        while (_loop)
        {
            try
            {
                var receiver = _receiver;
                if (receiver is { IsBound: true })
                {
                    int len = receiver.Receive(array);
                    _consecutiveReceiveErrors = 0;
                    _consecutiveStaleTimeouts = 0;
                    _lastPacketTimestamp = Stopwatch.GetTimestamp();
                    if (Interlocked.Exchange(ref _staleStateActive, 0) == 1)
                        _logger.LogInformation("BabbleEyeOSC data flow recovered.");
                    var packetCount = Interlocked.Increment(ref _receivedPacketCount);
                    if (packetCount == 1 || Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReceiveLogTimestamp)) >= TimeSpan.FromSeconds(5))
                    {
                        Interlocked.Exchange(ref _lastReceiveLogTimestamp, Stopwatch.GetTimestamp());
                        _logger.LogInformation("BabbleEyeOSC receive stats: packets={packets}", packetCount);
                    }

                    int messageIndex = 0;
                    OscMessage oscMessage;
                    try
                    {
                        oscMessage = new OscMessage(array, len, ref messageIndex);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (oscMessage.Address == FrameReadyAddress)
                    {
                        HandleFrameReady();
                        continue;
                    }

                    if (oscMessage.Value is float value)
                    {
                        var updated = false;
                        switch (oscMessage.Address)
                        {
                            case "/LeftEyeX":
                                EyeExpressions[(int)ExpressionMapping.EyeLeftX] = value;
                                UnifiedTracking.Data.Eye.Left.Gaze.x = value;
                                updated = true;
                                break;
                            case "/LeftEyeY":
                                EyeExpressions[(int)ExpressionMapping.EyeLeftY] = value;
                                UnifiedTracking.Data.Eye.Left.Gaze.y = value;
                                updated = true;
                                break;
                            case "/LeftEyeLid":
                                EyeExpressions[(int)ExpressionMapping.EyeLeftLid] = value;
                                UnifiedTracking.Data.Eye.Left.Openness = value;
                                updated = true;
                                break;
                            case "/LeftEyeWiden":
                                EyeExpressions[(int)ExpressionMapping.EyeLeftWiden] = value;
                                UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.EyeWideLeft].Weight = value;
                                updated = true;
                                break;
                            // case "/LeftEyeLower":
                            //     EyeExpressions[(int)ExpressionMapping.EyeLeftLower] = value;
                            //     break;
                            case "/LeftEyeBrow":
                                EyeExpressions[(int)ExpressionMapping.EyeLeftSquint] = value;
                                updated = true;
                                break;
                            case "/RightEyeX":
                                EyeExpressions[(int)ExpressionMapping.EyeRightX] = value;
                                UnifiedTracking.Data.Eye.Right.Gaze.x = value;
                                updated = true;
                                break;
                            case "/RightEyeY":
                                EyeExpressions[(int)ExpressionMapping.EyeRightY] = value;
                                UnifiedTracking.Data.Eye.Right.Gaze.y = value;
                                updated = true;
                                break;
                            case "/RightEyeLid":
                                EyeExpressions[(int)ExpressionMapping.EyeRightLid] = value;
                                UnifiedTracking.Data.Eye.Right.Openness = value;
                                updated = true;
                                break;
                            case "/RightEyeWiden":
                                EyeExpressions[(int)ExpressionMapping.EyeRightWiden] = value;
                                UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.EyeWideRight].Weight = value;
                                updated = true;
                                break;
                            // case "/RightEyeLower":
                            //     EyeExpressions[(int)ExpressionMapping.EyeRightLower] = value;
                            //     break;
                            case "/RightEyeBrow":
                                EyeExpressions[(int)ExpressionMapping.EyeRightSquint] = value;
                                updated = true;
                                break;
                            default:
                                if (BabbleExpressions.AddressToExpressions.TryGetValue(oscMessage.Address, out var mappedExpressions))
                                {
                                    BabbleExpressions.BabbleExpressionMap.SetByKey2(oscMessage.Address, value);
                                    foreach (var expression in mappedExpressions)
                                    {
                                        UnifiedTracking.Data.Shapes[(int)expression].Weight = value;
                                    }
                                    updated = true;
                                }
                                break;
                        }

                        if (updated)
                            HandleDataUpdated();
                    }
                }
                else
                {
                    RecreateReceiver();
                    Thread.Sleep(RebindBackoff);
                }
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
            {
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPacketTimestamp)) >= StaleDataThreshold)
                {
                    var rebindReceiver = Interlocked.Increment(ref _consecutiveStaleTimeouts) >= 2;
                    HandleStaleData(rebindReceiver);
                }
                continue;
            }
            catch (ObjectDisposedException) when (_loop)
            {
                RecreateReceiver();
                Thread.Sleep(RebindBackoff);
            }
            catch (SocketException ex) when (_loop)
            {
                _consecutiveReceiveErrors++;
                if (_consecutiveReceiveErrors == 1 || _consecutiveReceiveErrors % 20 == 0)
                    _logger.LogWarning(ex, "BabbleEyeOSC receive socket fault. Rebinding receiver.");

                RecreateReceiver();
                Thread.Sleep(RebindBackoff);
            }
            catch (Exception ex) when (_loop)
            {
                _consecutiveReceiveErrors++;
                if (_consecutiveReceiveErrors == 1 || _consecutiveReceiveErrors % 20 == 0)
                    _logger.LogWarning(ex, "BabbleEyeOSC unexpected receive fault. Rebinding receiver.");

                RecreateReceiver();
                Thread.Sleep(RebindBackoff);
            }
        }
    }

    private void HandleStaleData(bool rebindReceiver)
    {
        var hadFrameSync = Interlocked.Exchange(ref _frameSyncActive, 0) == 1;
        Interlocked.Exchange(ref _pendingImmediateUpdate, 0);
        if (Interlocked.Exchange(ref _staleStateActive, 1) == 0)
        {
            _logger.LogWarning("BabbleEyeOSC data flow stalled. Neutralizing eye state{rebindSuffix}{frameSyncSuffix}.",
                rebindReceiver ? " and rebinding receiver" : string.Empty,
                hadFrameSync ? " and disabling frame sync" : string.Empty);
        }
        else if (rebindReceiver)
        {
            _logger.LogWarning("BabbleEyeOSC data flow remains stalled. Rebinding receiver.");
        }

        NeutralizeEyeState();
        _onDataUpdated?.Invoke();
        if (rebindReceiver)
        {
            RecreateReceiver();
            Thread.Sleep(RebindBackoff);
        }
    }

    private static void NeutralizeEyeState()
    {
        EyeExpressions[(int)ExpressionMapping.EyeLeftX] = 0f;
        EyeExpressions[(int)ExpressionMapping.EyeLeftY] = 0f;
        EyeExpressions[(int)ExpressionMapping.EyeLeftLid] = 1f;
        EyeExpressions[(int)ExpressionMapping.EyeLeftWiden] = 0f;
        EyeExpressions[(int)ExpressionMapping.EyeLeftSquint] = 0f;
        EyeExpressions[(int)ExpressionMapping.EyeRightX] = 0f;
        EyeExpressions[(int)ExpressionMapping.EyeRightY] = 0f;
        EyeExpressions[(int)ExpressionMapping.EyeRightLid] = 1f;
        EyeExpressions[(int)ExpressionMapping.EyeRightWiden] = 0f;
        EyeExpressions[(int)ExpressionMapping.EyeRightSquint] = 0f;

        UnifiedTracking.Data.Eye.Left.Gaze.x = 0f;
        UnifiedTracking.Data.Eye.Left.Gaze.y = 0f;
        UnifiedTracking.Data.Eye.Left.Openness = 1f;
        UnifiedTracking.Data.Eye.Right.Gaze.x = 0f;
        UnifiedTracking.Data.Eye.Right.Gaze.y = 0f;
        UnifiedTracking.Data.Eye.Right.Openness = 1f;
        UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.EyeWideLeft].Weight = 0f;
        UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.EyeWideRight].Weight = 0f;
        UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.EyeSquintLeft].Weight = 0f;
        UnifiedTracking.Data.Shapes[(int)UnifiedExpressions.EyeSquintRight].Weight = 0f;
    }

    private void HandleDataUpdated()
    {
        if (Volatile.Read(ref _frameSyncActive) == 1)
        {
            var elapsed = Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastFrameReadyTimestamp));
            if (!(elapsed > FrameReadyTimeout))
            {
                Interlocked.Exchange(ref _pendingImmediateUpdate, 1);
                return;
            }

            if (Interlocked.Exchange(ref _frameSyncActive, 0) == 1)
            {
                _logger.LogWarning("BabbleEyeOSC frame sync timed out after {elapsedMs} ms. Falling back to per-packet updates until frameReady resumes.",
                    elapsed.TotalMilliseconds);
            }
            Interlocked.Exchange(ref _pendingImmediateUpdate, 0);
        }

        _onDataUpdated?.Invoke();
    }

    private void HandleFrameReady()
    {
        var timestamp = Stopwatch.GetTimestamp();
        var wasInactive = Interlocked.Exchange(ref _frameSyncActive, 1) == 0;
        Interlocked.Exchange(ref _lastFrameReadyTimestamp, timestamp);
        if (wasInactive && Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPacketTimestamp)) < TimeSpan.FromSeconds(2))
        {
            _logger.LogInformation("BabbleEyeOSC frame sync resumed.");
        }
        var frameReadyCount = Interlocked.Increment(ref _frameReadyCount);
        if (frameReadyCount == 1 || Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastFrameReadyLogTimestamp)) >= TimeSpan.FromSeconds(5))
        {
            Interlocked.Exchange(ref _lastFrameReadyLogTimestamp, timestamp);
            _logger.LogInformation("BabbleEyeOSC frame sync stats: frameReady={frameReady} packets={packets}", frameReadyCount, Volatile.Read(ref _receivedPacketCount));
        }

        if (Interlocked.Exchange(ref _pendingImmediateUpdate, 0) == 1)
            _onDataUpdated?.Invoke();
    }

    public void Teardown()
    {
        _loop = false;
        lock (_receiverLock)
        {
            _receiver?.Close();
            _receiver?.Dispose();
        }
        _thread!.Join();
    }
}
