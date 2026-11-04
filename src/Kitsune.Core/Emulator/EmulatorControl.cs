using System.Threading.Channels;
using Android.Emulation.Control;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;

namespace Kitsune.Core.Emulator;

/// <summary>
/// Client for the emulator's gRPC control service. Input goes through one long-lived client-streaming
/// call, which is the lowest-latency path the emulator offers: events are written straight into the
/// virtual touchscreen and keyboard with no adb round trip.
/// </summary>
public sealed class EmulatorControl : IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly EmulatorController.EmulatorControllerClient _client;
    private readonly Channel<InputEvent> _queue = Channel.CreateUnbounded<InputEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private Task? _pump;

    public int Port { get; }
    public EmulatorController.EmulatorControllerClient Client => _client;

    public EmulatorControl(int grpcPort)
    {
        Port = grpcPort;
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{grpcPort}", new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
            },
        });
        _client = new EmulatorController.EmulatorControllerClient(_channel);
    }

    public async Task<EmulatorStatus> GetStatusAsync(CancellationToken ct = default) =>
        await _client.getStatusAsync(new Empty(), cancellationToken: ct);

    public async Task<DisplayConfigurations> GetDisplaysAsync(CancellationToken ct = default) =>
        await _client.getDisplayConfigurationsAsync(new Empty(), cancellationToken: ct);

    /// <summary>Waits until the gRPC endpoint answers, then until the guest reports boot completed.</summary>
    public async Task<EmulatorStatus> WaitForBootAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        EmulatorStatus? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                last = await _client.getStatusAsync(new Empty(), deadline: DateTime.UtcNow.AddSeconds(2), cancellationToken: ct);
                if (last.Booted) return last;
            }
            catch (RpcException) { }
            await Task.Delay(500, ct);
        }
        throw new TimeoutException(last is null ? "The emulator control endpoint never came up." : "The guest did not finish booting in time.");
    }

    public async Task ShutdownAsync(CancellationToken ct = default) =>
        await _client.setVmStateAsync(new VmRunState { State = VmRunState.Types.RunState.Shutdown }, cancellationToken: ct);

    public async Task<byte[]> ScreenshotPngAsync(int display = 0, CancellationToken ct = default)
    {
        var img = await _client.getScreenshotAsync(new ImageFormat { Format = ImageFormat.Types.ImgFormat.Png, Display = (uint)display }, cancellationToken: ct);
        return img.Image_.ToByteArray();
    }

    /// <summary>Queues an input event on the streaming call. Never blocks the caller.</summary>
    public void Send(InputEvent ev)
    {
        EnsurePump();
        _queue.Writer.TryWrite(ev);
    }

    public void SendTouches(IEnumerable<Touch> touches, int display = 0)
    {
        var ev = new TouchEvent { Display = display };
        ev.Touches.AddRange(touches);
        Send(new InputEvent { TouchEvent = ev });
    }

    public void SendTouch(int id, int x, int y, bool down, int display = 0) =>
        SendTouches([new Touch { Identifier = id, X = x, Y = y, Pressure = down ? 1 : 0, Expiration = Touch.Types.EventExpiration.NeverExpire }], display);

    /// <summary>Sends an Android key by Linux evdev code (e.g. KEY_BACK = 158, KEY_HOMEPAGE = 172).</summary>
    public void SendEvdevKey(int evdevCode, bool down) =>
        Send(new InputEvent
        {
            KeyEvent = new KeyboardEvent
            {
                CodeType = KeyboardEvent.Types.KeyCodeType.Evdev,
                EventType = down ? KeyboardEvent.Types.KeyEventType.Keydown : KeyboardEvent.Types.KeyEventType.Keyup,
                KeyCode = evdevCode,
            },
        });

    public void SendText(string text) =>
        Send(new InputEvent { KeyEvent = new KeyboardEvent { Text = text, EventType = KeyboardEvent.Types.KeyEventType.Keypress } });

    private void EnsurePump()
    {
        if (_pump is not null) return;
        lock (_queue)
        {
            _pump ??= Task.Run(PumpAsync);
        }
    }

    private async Task PumpAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var call = _client.streamInputEvent(cancellationToken: ct);
                await foreach (var ev in _queue.Reader.ReadAllAsync(ct))
                    await call.RequestStream.WriteAsync(ev, ct);
                await call.RequestStream.CompleteAsync();
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (RpcException)
            {
                // Emulator not up yet or the stream dropped: back off and reopen; queued events are kept.
                try { await Task.Delay(250, ct); } catch (OperationCanceledException) { return; }
            }
            catch (Exception)
            {
                try { await Task.Delay(500, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();
        if (_pump is not null) { try { await _pump; } catch { } }
        _channel.Dispose();
        _cts.Dispose();
    }
}
