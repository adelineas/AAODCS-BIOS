using System.Collections.Concurrent;

namespace AaoDcsBiosRuntimeBridge;

/// <summary>
/// Periodically emits DCS-BIOS export frames (SYNC every ~33ms) like the real DCS-BIOS export stream.
/// Within each frame we include any queued write-access records plus an optional keepalive u16 write.
/// </summary>
public sealed class DcsBiosHubSender : IDisposable
{
    private readonly SerialPortShell _port;
    private readonly ConcurrentQueue<byte[]> _pendingWriteAccess = new();
    private readonly PeriodicTimer _timer;
    private readonly CancellationTokenSource _cts = new();

    private readonly bool _keepAliveEnabled;
    private readonly ushort _keepAliveAddress;
    private readonly ushort _keepAliveValue;

    public DcsBiosHubSender(
        SerialPortShell port,
        double framePeriodMs = 33.333,
        bool keepAliveEnabled = true,
        ushort keepAliveAddress = 0xFFFE,
        ushort keepAliveValue = 0x0000)
    {
        _port = port;
        _timer = new PeriodicTimer(TimeSpan.FromMilliseconds(framePeriodMs));
        _keepAliveEnabled = keepAliveEnabled;
        _keepAliveAddress = keepAliveAddress;
        _keepAliveValue = keepAliveValue;

        _ = Task.Run(RunAsync);
    }

    public void EnqueueWriteAccess(byte[] writeAccess)
    {
        if (writeAccess is null || writeAccess.Length == 0) return;
        _pendingWriteAccess.Enqueue(writeAccess);
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _timer.Dispose(); } catch { /* ignore */ }
        try { _cts.Dispose(); } catch { /* ignore */ }
    }

    private async Task RunAsync()
    {
        try
        {
            while (await _timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                if (!_port.IsOpen) continue;

                // Drain queued write-access records
                var list = new List<byte[]>(8);
                while (_pendingWriteAccess.TryDequeue(out var wa))
                    list.Add(wa);

                if (_keepAliveEnabled)
                    list.Add(DcsBiosFrames.MakeWriteAccessU16(_keepAliveAddress, _keepAliveValue));

                var frame = DcsBiosFrames.MakeFrame(list);
                _port.EnqueueWrite(frame);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch
        {
            // ignore; port may have died
        }
    }
}
