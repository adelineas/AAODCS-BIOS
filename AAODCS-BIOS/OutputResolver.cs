using System.Linq;
using System.Globalization;

namespace AaoDcsBiosRuntimeBridge;

public enum OutputKind { Bit, String }

public sealed class ResolvedOutputMapping
{
    public string Name { get; init; } = "";
    public string Source { get; init; } = "";
    public List<string> Sources { get; init; } = new();
    public OutputKind Kind { get; init; }
    public ushort Address { get; init; }

    // Bit
    public ushort Mask { get; init; }
    public double Threshold { get; init; } = 0.5;
    public bool Invert { get; init; } = false;

    // String
    public int MaxLen { get; init; } = 0;
    public FormatSpec? Format { get; init; }

    public StringFitSpec? Str { get; init; }

    // If true, at least one source is an AAO string expression (", String"). Those must be
    // polled via AAO getstringvars instead of getvars.
    public bool HasStringSources { get; init; } = false;

    public List<string> Targets { get; init; } = new();

    public bool EvaluateBool(double v)
    {
        bool on = v > Threshold;
        return Invert ? !on : on;
    }

    public string? FormatStringArgs(params object[] args)
{
    if (MaxLen <= 0) return null;
    if (args is null || args.Length == 0) return null;

    var spec = Format;
    var fit = Str;

    // Determine the effective display window inside the DCS-BIOS buffer.
    // Default: use the DCS-BIOS max_length for this output.
    int width = (fit?.width is int w && w > 0) ? Math.Min(w, MaxLen) : MaxLen;

    // Apply optional global numeric transform (scale/offset/round) BEFORE templating.
    object[] vals = new object[args.Length];
    for (int i = 0; i < args.Length; i++)
    {
        var a = args[i];
        if (a is null)
        {
            vals[i] = "";
            continue;
        }

        if (a is string ss)
        {
            vals[i] = UnescapeByteEscapes(ss);
            continue;
        }

        // Numeric values: keep as double so template can apply per-placeholder formats ({1:000.000})
        if (a is double dd)
        {
            vals[i] = ApplyNumericTransform(dd, spec);
            continue;
        }

        // Fallback
        vals[i] = Convert.ToString(a, CultureInfo.InvariantCulture) ?? "";
    }

    string composed;

    if (spec is not null && !string.IsNullOrWhiteSpace(spec.template))
    {
        try
        {
            composed = string.Format(CultureInfo.InvariantCulture, spec.template!, vals);
        }
        catch
        {
            composed = string.Concat(vals.Select(x => x?.ToString() ?? string.Empty));
        }
    }
    else
    {
        // No template. Single arg -> render as string. Multi -> concat.
        if (vals.Length == 1)
        {
            var v0 = vals[0];
            if (v0 is double dv && spec is not null && !string.IsNullOrWhiteSpace(spec.numFormat))
            {
                composed = dv.ToString(spec.numFormat, CultureInfo.InvariantCulture);
            }
            else
            {
                composed = v0?.ToString() ?? "";
            }
        }
        else
        {
            composed = string.Concat(vals.Select(x => x?.ToString() ?? string.Empty));
        }
    }

    composed = UnescapeByteEscapes(composed);

    // Fit to the display window width (clip + pad), then pad the remainder of the DCS-BIOS buffer with spaces.
    string fitted = FitToWidth(composed, width, fit);

    // Ensure the final transport string is exactly MaxLen characters.
    if (MaxLen > width)
        fitted = fitted.PadRight(MaxLen, ' ');

    return EnsureMaxLen(fitted, MaxLen);
}

private static double ApplyNumericTransform(double v, FormatSpec? spec)
{
    if (spec is null) return v;

    var x = v;
    if (spec.offset is not null) x += spec.offset.Value;
    if (spec.scale is not null) x *= spec.scale.Value;

    if (string.IsNullOrWhiteSpace(spec.round))
        return x;

    var r = spec.round!.Trim().ToLowerInvariant();
    return r switch
    {
        "floor" => Math.Floor(x),
        "ceil" => Math.Ceiling(x),
        "truncate" => Math.Truncate(x),
        _ => Math.Round(x, 6, MidpointRounding.AwayFromZero) // keep precision; final format decides decimals
    };
}

private static string FitToWidth(string s, int width, StringFitSpec? fit)
{
    if (width <= 0) return s ?? string.Empty;
    s ??= string.Empty;

    var clipSide = (fit?.clipSide ?? "right").Trim().ToLowerInvariant();
    var padSide = (fit?.padSide ?? "right").Trim().ToLowerInvariant();
    char padChar = (fit?.padChar is { Length: 1 }) ? fit!.padChar![0] : ' ';

    if (s.Length > width)
    {
        return clipSide == "left" ? s[..width] : s[^width..];
    }

    if (s.Length < width)
    {
        return padSide == "left" ? s.PadLeft(width, padChar) : s.PadRight(width, padChar);
    }

    return s;
}

private static string EnsureMaxLen(string s, int maxLen)
    {
        if (string.IsNullOrEmpty(s))
        {
            return new string(' ', maxLen);
        }

        if (s.Length > maxLen) return s[..maxLen];
        if (s.Length < maxLen) return s.PadRight(maxLen, ' '); // transport-level clear
        return s;
    }

    // NOTE: we intentionally do not provide "default pad" behavior here.
    // Padding is only applied when digits is configured and padChar is explicit.

    private static string UnescapeByteEscapes(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;

        // Fast path: nothing to do
        if (!s.Contains("\\x", StringComparison.OrdinalIgnoreCase) && !s.Contains("\\\\"))
            return s;

        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\' || i == s.Length - 1)
            {
                sb.Append(c);
                continue;
            }

            char n = s[i + 1];

            // "\\\\" -> "\\"
            if (n == '\\')
            {
                sb.Append('\\');
                i++;
                continue;
            }

            // "\\xHH" / "\\XHH" -> single byte char 0xHH (later encoded as Latin-1)
            if ((n == 'x' || n == 'X') && i + 3 < s.Length)
            {
                int hi = HexNibble(s[i + 2]);
                int lo = HexNibble(s[i + 3]);
                if (hi >= 0 && lo >= 0)
                {
                    sb.Append((char)((hi << 4) | lo));
                    i += 3;
                    continue;
                }
            }

            // Unknown escape -> keep as-is (including the backslash)
            sb.Append('\\');
        }
        return sb.ToString();
    }

    private static int HexNibble(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return 10 + (c - 'a');
        if (c >= 'A' && c <= 'F') return 10 + (c - 'A');
        return -1;
    }
}

public static class OutputResolver
{
    private static bool IsAaoExpression(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        return s.TrimStart().StartsWith("(", StringComparison.Ordinal);
    }

    public static ResolvedOutputMapping? Resolve(OutputMapping m, DcsControl ctrl)
    {
        var name = string.IsNullOrWhiteSpace(m.name) ? m.dcs : m.name!.Trim();
        var source = m.source;

        var mode = (m.mode ?? "").Trim().ToLowerInvariant();

        // How we decide between BIT vs STRING:
        // - Explicit: mode="string" or a format object => string.
        // - Auto: if the DCS-BIOS control only provides a string export (typical for CDU/MFCD lines),
        //         treat it as string even if the user didn't specify mode/format.
        // This avoids having to add "mode":"string" for every line-style display mapping.
        bool hasString = ctrl.Outputs.Any(x => string.Equals(x.Type, "string", StringComparison.OrdinalIgnoreCase));
        bool hasBitInt = ctrl.Outputs.Any(x => string.Equals(x.Type, "integer", StringComparison.OrdinalIgnoreCase) && x.Mask is not null);
        bool wantString = mode == "string" || m.format is not null || (hasString && !hasBitInt);

        var sourcesList = new List<string>();
        if (m.sources is not null && m.sources.Length > 0)
        {
            foreach (var raw in m.sources)
            {
                if (raw is null) continue;
                // Preserve literal whitespace EXACTLY as configured (trailing spaces are meaningful for fixed-width displays).
                // Only trim AAO expressions (they start with '(' ignoring leading whitespace).
                if (IsAaoExpression(raw))
                {
                    var s = raw.Trim();
                    if (s.Length > 0) sourcesList.Add(s);
                }
                else
                {
                    // Keep as-is, even if it's only spaces.
                    sourcesList.Add(raw);
                }
            }
        }
        else if (source is not null)
        {
            if (IsAaoExpression(source))
            {
                var s = source.Trim();
                if (s.Length > 0) sourcesList.Add(s);
            }
            else
            {
                sourcesList.Add(source);
            }
        }

        if (wantString)
        {
            if (sourcesList.Count == 0) return null;
            var o = ctrl.Outputs.FirstOrDefault(x => string.Equals(x.Type, "string", StringComparison.OrdinalIgnoreCase));
            if (o is null || o.MaxLength is null) return null;
            return new ResolvedOutputMapping
            {
                Name = name,
                Source = source ?? sourcesList[0],
                Sources = sourcesList,
                Kind = OutputKind.String,
                Address = checked((ushort)o.Address),
                MaxLen = o.MaxLength.Value,
                Format = m.format,
                Str = m.str,
                HasStringSources = sourcesList.Any(IsAaoStringExpression),
                Targets = (m.targets ?? Array.Empty<string>()).ToList()
            };
        }
        else
        {
            var o = ctrl.Outputs.FirstOrDefault(x => string.Equals(x.Type, "integer", StringComparison.OrdinalIgnoreCase) && x.Mask is not null);
            if (o is null || o.Mask is null) return null;

            if (string.IsNullOrWhiteSpace(source)) return null;
            var thr = ParseDoubleFlexible(m.threshold ?? "0.5", 0.5);
            var inv = m.invert ?? false;

            return new ResolvedOutputMapping
            {
                Name = name,
                Source = source!,
                Sources = new List<string> { source! },
                Kind = OutputKind.Bit,
                Address = checked((ushort)o.Address),
                Mask = checked((ushort)o.Mask.Value),
                Threshold = thr,
                Invert = inv,
                Targets = (m.targets ?? Array.Empty<string>()).ToList()
            };
        }
    }

    private static bool IsAaoStringExpression(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        var t = s.Trim();
        if (!t.StartsWith("(", StringComparison.Ordinal)) return false; // literals like "ATC:" are not expressions
        return t.Contains(", String)", StringComparison.OrdinalIgnoreCase) || t.Contains(",String)", StringComparison.OrdinalIgnoreCase);
    }

    private static double ParseDoubleFlexible(string s, double fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var inv)) return inv;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out var cur)) return cur;

        var s2 = s.Replace(',', '.');
        if (double.TryParse(s2, NumberStyles.Float, CultureInfo.InvariantCulture, out inv)) return inv;

        return fallback;
    }
}