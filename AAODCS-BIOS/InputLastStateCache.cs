using System.Text.Json;

namespace AaoDcsBiosRuntimeBridge;

/// <summary>
/// Persists last seen values for selected stateful inputs (multi-position switches, on/off switches, axes).
/// The cache is opt-in per DCS identifier via input mapping property: "persist": "laststate".
///
/// On disk we store a simple JSON dictionary:
///   { "TACAN_MODE": "2", "PARK_BRAKE_SW": "1" }
///
/// We write the whole file atomically (temp + replace) and throttle disk writes.
/// </summary>
public sealed class InputLastStateCache
{
    private readonly object _lock = new();

    private Dictionary<string, string> _state = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;
    private DateTime _lastWriteUtc = DateTime.MinValue;

    public string FilePath { get; }
    public int FlushIntervalMs { get; }
    public bool Verbose { get; }

    public InputLastStateCache(string filePath, int flushIntervalMs = 1000, bool verbose = false)
    {
        FilePath = filePath;
        FlushIntervalMs = Math.Max(0, flushIntervalMs);
        Verbose = verbose;
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return;

            var txt = File.ReadAllText(FilePath);
            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(txt);
            if (dict is not null)
            {
                lock (_lock)
                {
                    _state = new Dictionary<string, string>(dict, StringComparer.OrdinalIgnoreCase);
                    _dirty = false;
                }
            }
        }
        catch (Exception ex)
        {
            // Non-fatal: start with empty cache.
            if (Verbose)
                Console.WriteLine($"WARN : LastState cache load failed (ignored): {ex.Message}");
        }
    }

    public bool TryGet(string dcs, out string value)
    {
        lock (_lock)
            return _state.TryGetValue(dcs, out value!);
    }

    public int Count
    {
        get { lock (_lock) return _state.Count; }
    }

    public void Update(string dcs, string arg)
    {
        if (string.IsNullOrWhiteSpace(dcs))
            return;

        lock (_lock)
        {
            // If value didn't change, don't mark dirty.
            if (_state.TryGetValue(dcs, out var prev) && string.Equals(prev, arg, StringComparison.Ordinal))
                return;

            _state[dcs] = arg;
            _dirty = true;
        }
    }

    /// <summary>
    /// Flush to disk if dirty and enough time passed. Use force=true on shutdown.
    /// </summary>
    public void MaybeFlush(bool force = false)
    {
        Dictionary<string, string>? snapshot = null;

        lock (_lock)
        {
            if (!_dirty)
                return;

            var now = DateTime.UtcNow;
            if (!force && FlushIntervalMs > 0 && (now - _lastWriteUtc).TotalMilliseconds < FlushIntervalMs)
                return;

            snapshot = new Dictionary<string, string>(_state, StringComparer.OrdinalIgnoreCase);
            _dirty = false;
            _lastWriteUtc = now;
        }

        try
        {
            WriteAtomic(snapshot);
        }
        catch (Exception ex)
        {
            // On failure, mark dirty again so we can retry later.
            lock (_lock) _dirty = true;

            if (Verbose)
                Console.WriteLine($"WARN : LastState cache write failed (will retry): {ex.Message}");
        }
    }

    private void WriteAtomic(Dictionary<string, string> snapshot)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tmp = FilePath + ".tmp";
        var bak = FilePath + ".bak";

        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });

        File.WriteAllText(tmp, json);

        if (File.Exists(FilePath))
        {
            // Atomic replace (Windows). Keeps a .bak which we can remove afterwards.
            File.Replace(tmp, FilePath, bak, ignoreMetadataErrors: true);
            try { File.Delete(bak); } catch { /* ignore */ }
        }
        else
        {
            File.Move(tmp, FilePath, overwrite: true);
        }
    }
}
