using System.Globalization;
using System.Linq;

namespace AaoDcsBiosRuntimeBridge;

public enum InputActionKind { Trigger, SetVar, Script, Button }

public sealed class InputAction
{
    public InputActionKind Kind { get; init; }
    public string Name { get; init; } = "";     // trigger evt or setvar var
    public double Value { get; init; } = 1;     // trigger/setvar numeric value
    public string? Code { get; init; }          // script code

    // button JSON (AAO)
    public int Dev { get; init; }
    public int Chn { get; init; }
    public int Btn { get; init; }
    public int Bval { get; init; }              // 127/0

    public string SourceLine { get; init; } = "";
    public string AaoSummary { get; init; } = "";
}

public sealed class InputEngine
{
    public event Action? ActionEnqueued;

    // Raised when a stateful input with persist:laststate is observed (dcs, rawArg).
    public event Action<string, string>? LastStateObserved;

    private readonly List<InputMapping> _mappings;
    private readonly bool _verbose;
    private readonly bool _logUnmatchedRx;
    private readonly bool _edgeNumericMatches;

    // Per-switch persistence (opt-in): if any mapping for a DCS identifier has persist:laststate,
    // we store the last numeric value and can replay it on next start.
    private readonly HashSet<string> _persistDcs;

    // Keyed by "<target>:<dcs>"; stores last raw argument seen (for edge-detect on RS-485).
    private readonly Dictionary<string, string> _lastArgByKey = new(StringComparer.OrdinalIgnoreCase);

    // For analog wildcards: per-mapping runtime state (deadband / rate limit).
    private readonly Dictionary<string, AnalogState> _analogState = new(StringComparer.OrdinalIgnoreCase);

    private sealed class AnalogState
    {
        public double? LastRaw { get; set; }
        public DateTime? LastSentUtc { get; set; }
    }

    private readonly record struct PlaceholderValues(double Raw, double Value, int Int, double Norm)
    {
        public string RawStr => FormatNumber(Raw);
        public string ValueStr => FormatNumber(Value);
        public string IntStr => Int.ToString(CultureInfo.InvariantCulture);
        public string PctStr => Int.ToString(CultureInfo.InvariantCulture);
        public string NormStr => FormatNumber(Norm);
    }

    private readonly Queue<InputAction> _queue = new();
    private readonly object _lock = new();

    public InputEngine(List<InputMapping> mappings, bool verbose, bool logUnmatchedRx, bool edgeNumericMatches)
    {
        _mappings = mappings ?? new();
        _verbose = verbose;
        _logUnmatchedRx = logUnmatchedRx;
        _edgeNumericMatches = edgeNumericMatches;

        _persistDcs = new HashSet<string>(
            _mappings.Where(m => PersistSpec.IsLastStateEnabled(m.persist))
                     .Select(m => m.dcs)
                     .Where(s => !string.IsNullOrWhiteSpace(s)),
            StringComparer.OrdinalIgnoreCase);
    }

    public void ProcessLine(string targetName, string line)
    {
        // Expected formats (DCS-BIOS text protocol):
        //   IDENTIFIER ACTION
        //   IDENTIFIER VALUE
        // Examples:
        //   ILS_PWR TOGGLE
        //   ILS_MHZ INC
        //   ILS_VOL 32768
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return;

        if (_verbose && _logUnmatchedRx)
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} RX  {targetName}: {line}");

        string dcs = parts[0];
        string arg = parts[1];

        // Try parse numeric (int + double)
        bool hasInt = TryParseIntFlexible(arg, out int numInt);
        bool hasDouble = TryParseDoubleFlexible(arg, out double numDouble);

        // Edge-detect numeric values (e.g. multi-position switches that report 0..n).
        // IMPORTANT: apply this ONCE per (target,dcs) message, NOT per mapping.
        // Otherwise the first mapping encountered (even if it doesn't match) would
        // update the last-arg cache and accidentally suppress the actual matching
        // mapping later in the loop.
        if (_edgeNumericMatches && hasInt)
        {
            string key = $"{targetName}:{dcs}";
            if (_lastArgByKey.TryGetValue(key, out var prevArg) && string.Equals(prevArg, arg, StringComparison.Ordinal))
                return;
            _lastArgByKey[key] = arg;
        }

        InputMapping? wildcard = null;
        InputMapping? exact = null;

        foreach (var m in _mappings)
        {
            if (!string.Equals(m.dcs, dcs, StringComparison.OrdinalIgnoreCase))
                continue;

            string match = (m.match ?? "*").Trim();

            if (string.Equals(match, "*", StringComparison.OrdinalIgnoreCase))
            {
                wildcard ??= m;
                continue;
            }

            if (string.Equals(match, arg, StringComparison.OrdinalIgnoreCase))
            {
                exact = m;
                break; // most specific match wins
            }
        }

        var chosen = exact ?? wildcard;
        if (chosen is null)
            return;

        // Wildcard expects numeric values (potentiometers/axes). If not numeric, ignore.
        if (string.Equals((chosen.match ?? "*").Trim(), "*", StringComparison.OrdinalIgnoreCase) && !hasDouble)
            return;

        double raw = hasDouble ? numDouble : (hasInt ? numInt : 0.0);

        // Optional analog filtering: deadband and/or rate limiting
        if (chosen.filter is not null)
        {
            string stateKey = BuildStateKey(targetName, chosen);
            _analogState.TryGetValue(stateKey, out var st);
            st ??= new AnalogState();

            var now = DateTime.UtcNow;

            if (chosen.filter.rateLimitMs is int rl && rl > 0)
            {
                if (st.LastSentUtc is DateTime last && (now - last).TotalMilliseconds < rl)
                    return;
            }

            if (chosen.filter.deadband is double db && db > 0 && st.LastRaw.HasValue)
            {
                if (Math.Abs(raw - st.LastRaw.Value) < db)
                    return;
            }
        }

        // Optional mapping: remap raw input range to output range for scripts/setvars.
        double mapped = raw;
        int mappedInt = hasInt ? numInt : (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        double norm = raw;

        if (chosen.map is not null)
        {
            if (!TryApplyMap(chosen.map, raw, out mapped, out mappedInt, out norm))
                return; // invalid map (should have been caught in validation)
        }
        else
        {
            if (raw >= 0.0 && raw <= 1.0) norm = raw;
        }

        var pv = new PlaceholderValues(raw, mapped, mappedInt, norm);

        var act = BuildAction(chosen, line, pv);
        if (act is null) return;

        // Persist last seen numeric values for selected stateful inputs.
        if (_persistDcs.Contains(dcs) && (hasInt || hasDouble))
            LastStateObserved?.Invoke(dcs, arg);

        lock (_lock) _queue.Enqueue(act);
        ActionEnqueued?.Invoke();

        // Update analog state AFTER enqueue to make sure we don't "eat" the first event.
        if (chosen.filter is not null)
        {
            string stateKey = BuildStateKey(targetName, chosen);
            _analogState.TryGetValue(stateKey, out var st);
            st ??= new AnalogState();

            st.LastRaw = raw;
            st.LastSentUtc = DateTime.UtcNow;
            _analogState[stateKey] = st;
        }

        if (_verbose && !_logUnmatchedRx)
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} RX  {targetName}: {line}");
    }

    public bool TryDequeue(out InputAction action)
    {
        lock (_lock)
        {
            if (_queue.Count > 0)
            {
                action = _queue.Dequeue();
                return true;
            }
        }
        action = null!;
        return false;
    }

    private static InputAction? BuildAction(InputMapping m, string line, PlaceholderValues pv)
    {
        var a = m.aao;
        string type = (a.type ?? "trigger").Trim().ToLowerInvariant();

        if (type == "trigger")
        {
            if (string.IsNullOrWhiteSpace(a.name)) return null;
            double v = a.value ?? pv.Value; // if value not specified, pass mapped value
            return new InputAction
            {
                Kind = InputActionKind.Trigger,
                Name = a.name.Trim(),
                Value = v,
                SourceLine = line,
                AaoSummary = $"TRIGGER evt='{a.name}' value={v:0.###}"
            };
        }

        if (type == "setvar")
        {
            if (string.IsNullOrWhiteSpace(a.name)) return null;
            double v = a.value ?? pv.Value;
            return new InputAction
            {
                Kind = InputActionKind.SetVar,
                Name = a.name.Trim(),
                Value = v,
                SourceLine = line,
                AaoSummary = $"SETVAR var='{a.name}' value={v:0.###}"
            };
        }

        if (type == "script")
        {
            if (string.IsNullOrWhiteSpace(a.code)) return null;

            string code = ReplacePlaceholders(a.code, pv);
            return new InputAction
            {
                Kind = InputActionKind.Script,
                Code = code,
                SourceLine = line,
                AaoSummary = $"SCRIPT code='{Shorten(code)}'"
            };
        }

        if (type == "button")
        {
            if (a.dev is null || a.chn is null || a.btn is null) return null;
            int bval = a.bval ?? 127;
            return new InputAction
            {
                Kind = InputActionKind.Button,
                Dev = a.dev.Value,
                Chn = a.chn.Value,
                Btn = a.btn.Value,
                Bval = bval,
                SourceLine = line,
                AaoSummary = $"BUTTON dev={a.dev} chn={a.chn} btn={a.btn} bval={bval}"
            };
        }

        return null;
    }

    private static string BuildStateKey(string targetName, InputMapping m)
        => $"{targetName}|{m.SourceFile}|{m.dcs}|{m.name}|{m.match}|{m.aao.type}|{m.aao.name}|{m.aao.code}";

    private static string ReplacePlaceholders(string code, PlaceholderValues pv)
    {
        // Backwards compat: {value} existed before. Now:
        // {raw}  = raw numeric value as received
        // {value}= mapped numeric value (after map), or raw if no map
        // {int}  = mapped integer (rounded)
        // {pct}  = alias for {int} (useful when you map to 0..100)
        // {norm} = normalized raw value (0..1) computed from map.inMin/inMax
        return code
            .Replace("{raw}", pv.RawStr, StringComparison.OrdinalIgnoreCase)
            .Replace("{value}", pv.ValueStr, StringComparison.OrdinalIgnoreCase)
            .Replace("{int}", pv.IntStr, StringComparison.OrdinalIgnoreCase)
            .Replace("{pct}", pv.PctStr, StringComparison.OrdinalIgnoreCase)
            .Replace("{norm}", pv.NormStr, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryApplyMap(InputMapSpec map, double raw, out double mapped, out int mappedInt, out double norm)
    {
        mapped = raw;
        mappedInt = (int)Math.Round(raw, MidpointRounding.AwayFromZero);
        norm = raw;

        if (map.inMin is null || map.inMax is null || map.outMin is null || map.outMax is null)
            return false;

        double inMin = map.inMin.Value;
        double inMax = map.inMax.Value;
        double outMin = map.outMin.Value;
        double outMax = map.outMax.Value;

        double denom = (inMax - inMin);
        if (Math.Abs(denom) < 1e-12)
            return false;

        bool clamp = map.clamp ?? false;

        double r = raw;
        if (clamp)
            r = Math.Min(Math.Max(r, Math.Min(inMin, inMax)), Math.Max(inMin, inMax));

        norm = (r - inMin) / denom;
        if (clamp)
            norm = Math.Min(1.0, Math.Max(0.0, norm));

        mapped = outMin + norm * (outMax - outMin);

        if (clamp)
            mapped = Math.Min(Math.Max(mapped, Math.Min(outMin, outMax)), Math.Max(outMin, outMax));

        string round = (map.round ?? "nearest").Trim().ToLowerInvariant();
        mappedInt = round switch
        {
            "floor" => (int)Math.Floor(mapped),
            "ceil" => (int)Math.Ceiling(mapped),
            "truncate" => (int)Math.Truncate(mapped),
            _ => (int)Math.Round(mapped, MidpointRounding.AwayFromZero)
        };

        return true;
    }

    private static string FormatNumber(double v)
        => v.ToString("0.################", CultureInfo.InvariantCulture);

    private static string Shorten(string s)
        => s.Length <= 80 ? s : s[..77] + "...";

    private static bool TryParseDoubleFlexible(string s, out double value)
    {
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return true;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out value)) return true;
        return false;
    }

    private static bool TryParseIntFlexible(string s, out int value)
    {
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.CurrentCulture, out value)) return true;
        return false;
    }
}
