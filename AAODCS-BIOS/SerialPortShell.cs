using System.Collections.Concurrent;
using System.IO.Ports;
using System.Text;

namespace AaoDcsBiosRuntimeBridge;

/// <summary>
/// Serial port wrapper modeled after DCSBIOSBridge.
/// - Single writer thread (no concurrent writes).
/// - DataReceived-based reader that assembles ASCII lines (DCS-BIOS inputs) terminated by '\n'.
/// </summary>
public sealed class SerialPortShell : IDisposable
{
    private readonly SerialPort _sp;
    private readonly ConcurrentQueue<byte[]> _writeQueue = new();
    private readonly AutoResetEvent _writeSignal = new(false);
    private readonly Thread _writerThread;
    private volatile bool _shutdown;

    private readonly bool _rxEnabled;
    private readonly SemaphoreSlim _rxSemaphore = new(1, 1);
    private readonly StringBuilder _incoming = new(256);

    private long _txBytes;
    private long _rxBytes;
    public long TxBytes => System.Threading.Interlocked.Read(ref _txBytes);
    public long RxBytes => System.Threading.Interlocked.Read(ref _rxBytes);

    public string TargetName { get; }
    public string PortName => _sp.PortName;
    public bool IsOpen => _sp.IsOpen;

    public event Action<string, string>? LineReceived;

    public SerialPortShell(SerialTarget cfg)
    {
        TargetName = cfg.Name;
        _rxEnabled = cfg.Rx;

        _sp = new SerialPort(cfg.Port, cfg.Baud)
        {
            Parity = Parity.None,
            StopBits = StopBits.One,
            DataBits = 8,
            Handshake = Handshake.None,

            DtrEnable = cfg.DtrEnable,
            RtsEnable = cfg.RtsEnable,

            // DCSBIOSBridge treats 0 as InfiniteTimeout; we keep it Infinite by default.
            WriteTimeout = SerialPort.InfiniteTimeout,
            ReadTimeout = SerialPort.InfiniteTimeout,

            ReadBufferSize = 4096,
            WriteBufferSize = 4096,
            ReceivedBytesThreshold = 1,

            Encoding = Encoding.ASCII,
            NewLine = "\n",
        };

        if (_rxEnabled)
        {
            _sp.DataReceived += OnDataReceived;
            _sp.ErrorReceived += OnErrorReceived;
        }

        _writerThread = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = $"SerialWriter_{cfg.Name}_{cfg.Port}"
        };
    }

    public void Open()
    {
        _sp.Open();
        try { _sp.DiscardInBuffer(); } catch { }
        try { _sp.DiscardOutBuffer(); } catch { }

        _shutdown = false;
        _writerThread.Start();
    }

    public void EnqueueWrite(byte[] data)
    {
        if (data is null || data.Length == 0) return;
        if (!_sp.IsOpen) return;

        _writeQueue.Enqueue(data);
        _writeSignal.Set();
    }

    private void WriterLoop()
    {
        while (true)
        {
            if (_shutdown) break;

            _writeSignal.WaitOne(250);

            while (!_shutdown && _writeQueue.TryDequeue(out var data))
            {
                try
                {
                    // Match DCSBIOSBridge: use BaseStream.Write
                    _sp.BaseStream.Write(data, 0, data.Length);
                    System.Threading.Interlocked.Add(ref _txBytes, data.Length);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WARN : Serial write failed on {TargetName}/{PortName}: {ex.Message}");
                    _shutdown = true;
                    break;
                }
            }
        }
    }

    private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        // Surface once; keep running unless the port actually dies.
        Console.WriteLine($"WARN : Serial error on {TargetName}/{PortName}: {e.EventType}");
    }

    private void OnDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (!_rxEnabled) return;

        _rxSemaphore.Wait();
        try
        {
            var bytesToRead = _sp.BytesToRead;
            if (bytesToRead <= 0) return;

            var buf = new byte[bytesToRead];
            int read = _sp.BaseStream.Read(buf, 0, buf.Length);
            if (read <= 0) return;
            System.Threading.Interlocked.Add(ref _rxBytes, read);

            _incoming.Append(_sp.Encoding.GetString(buf, 0, read));

            // Process complete lines (ending in '\n')
            while (true)
            {
                var s = _incoming.ToString();
                int idx = s.IndexOf('\n');
                if (idx < 0) break;

                var line = s.Substring(0, idx);
                _incoming.Clear();
                if (idx + 1 < s.Length)
                    _incoming.Append(s.Substring(idx + 1));

                line = line.Trim();
                if (line.Length == 0) continue;

                try
                {
                    LineReceived?.Invoke(TargetName, line);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WARN : LineReceived handler failed: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WARN : Serial read failed on {TargetName}/{PortName}: {ex.Message}");
        }
        finally
        {
            try { _rxSemaphore.Release(); } catch { }
        }
    }

    public void Dispose()
    {
        _shutdown = true;
        try { _writeSignal.Set(); } catch { }

        if (_rxEnabled)
        {
            try { _sp.DataReceived -= OnDataReceived; } catch { }
            try { _sp.ErrorReceived -= OnErrorReceived; } catch { }
        }

        try { if (_sp.IsOpen) _sp.Close(); } catch { }
        try { _sp.Dispose(); } catch { }

        try { _writeSignal.Dispose(); } catch { }
        try { _rxSemaphore.Dispose(); } catch { }
    }
}
