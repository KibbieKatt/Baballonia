using Baballonia.Contracts;
using Microsoft.Extensions.Logging;
using OscCore;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace Baballonia.Services;

/// <summary>
/// OscSendService is responsible for encoding osc messages and sending them over OSC
/// </summary>
public abstract class OscSendService(
    ILogger<OscSendService> logger,
    IOscTarget oscTarget)
{
    public event Action<int> OnMessagesDispatched = _ => { };
    protected readonly IOscTarget OscTarget = oscTarget;
    private Socket _sendSocket;
    private byte[] _sendBuffer = new byte[512];

    protected void UpdateTarget(IPEndPoint endpoint)
    {
        _sendSocket?.Close();
        OscTarget.IsConnected = false;

        _sendSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        try
        {
            _sendSocket.Connect(endpoint);
            OscTarget.IsConnected = true;
        }
        catch (SocketException ex)
        {
            logger.LogWarning("Failed to bind to sender endpoint: {IpEndPoint}. {ExMessage}", endpoint, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError("Unexpected Exception while binding to sender endpoint: {IpEndPoint}. {ExMessage}", endpoint, ex.Message);
        }
    }

    public async Task Send(OscMessage message, CancellationToken ct)
    {
        if (_sendSocket is not { Connected: true })
        {
            return;
        }

        try
        {
            await SendMessage(message, ct);
            OnMessagesDispatched(1);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending OSC message");
        }
    }

    public async Task Send(IReadOnlyList<OscMessage> messages, CancellationToken ct)
    {
        if (_sendSocket is not { Connected: true })
        {
            return;
        }

        try
        {
            for (var i = 0; i < messages.Count; i++)
            {
                await SendMessage(messages[i], ct);
            }

            OnMessagesDispatched(messages.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error sending OSC bundle");
        }
    }

    public Task Send(OscMessage[] messages, CancellationToken ct) => Send((IReadOnlyList<OscMessage>)messages, ct);

    private async Task SendMessage(OscMessage message, CancellationToken ct)
    {
        EnsureBufferSize(message.SizeInBytes);
        var length = message.Write(_sendBuffer, 0);
        await _sendSocket!.SendAsync(_sendBuffer.AsMemory(0, length), SocketFlags.None, ct);
    }

    private void EnsureBufferSize(int requiredLength)
    {
        if (_sendBuffer.Length >= requiredLength)
            return;

        Array.Resize(ref _sendBuffer, requiredLength);
    }
}
