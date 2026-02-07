using System.Text.Json;
using System.Text.Json.Serialization;

namespace AaoDcsBiosRuntimeBridge;

// JSONC supported (comments + trailing commas). Keep property names lower-case in files.

public sealed class PanelFile
{
    public List<OutputMapping> outputs { get; set; } = new();
    public List<InputMapping> inputs { get; set; } = new();

    public List<OutputMapping> Outputs => outputs;
    public List<InputMapping> Inputs => inputs;
}

public sealed class OutputMapping
{
    public string? name { get; set; }
    public string dcs { get; set; } = "";
    public string source { get; set; } = "";
    public string[]? sources { get; set; }  // optional: multiple sources for format
    public string? mode { get; set; }       // "bit" or "string" (optional)
    public FormatSpec? format { get; set; } // optional format spec (template/scale/offset/round/numFormat)
    public StringFitSpec? str { get; set; } // optional: final string fit (width/pad/clip)
    public string? threshold { get; set; }  // "0,5" or "0.5"
    public bool? invert { get; set; }
    public string[]? targets { get; set; }

    [JsonIgnore]
    public string SourceFile { get; set; } = "";

    public string Name => name ?? dcs;
    public string Dcs => dcs;
    public string Source => source;
    public string[]? Sources => sources;
}

// Universal formatter spec (JSONC). This is an identity transform unless you opt in.
// All fields are optional; if omitted, the bridge will emit the raw numeric value
// from AAO (InvariantCulture).
//
// IMPORTANT: DCS-BIOS string exports have a fixed length. The bridge always overwrites
// the full buffer, and will right-pad with spaces (transport-level) if your formatted
// string is shorter than the target length. If you need alignment, zero-fill, clipping,
// or an inserted decimal point, configure it explicitly (see formatting_rules.md).
public sealed class FormatSpec
{
    // Optional template for string.Format(CultureInfo.InvariantCulture, template, values...)
    // You can apply per-value numeric formatting directly in the template using standard
    // .NET format specifiers, e.g. "{1:000.000}" or "{2:000}".
    public string? template { get; set; }

    // Optional global numeric transform for numeric values before templating:
    // y = (x + offset) * scale
    public double? scale { get; set; }
    public double? offset { get; set; }

    // Optional rounding policy applied after transform (only affects numeric values):
    // "nearest" | "floor" | "ceil" | "truncate"
    public string? round { get; set; }

    // Optional numeric format applied when there is NO template and the output is a single numeric value.
    // This uses standard .NET numeric format strings ("F3", "000.000", "D5", etc.).
    public string? numFormat { get; set; }
}

public sealed class StringFitSpec
{
    // Optional display width inside the DCS-BIOS string buffer.
    // If omitted or <= 0, the bridge uses the DCS-BIOS max_length for this output.
    // If width < max_length, the bridge pads the remainder (max_length-width) with spaces to clear.
    public int? width { get; set; }

    // Clip side when the composed string exceeds width: "left" keeps leftmost chars, "right" keeps rightmost chars.
    public string? clipSide { get; set; } // default "right"

    // Padding side when the composed string is shorter than width:
    // "left" => left-pad (right aligned), "right" => right-pad (left aligned)
    public string? padSide { get; set; } // default "right"
    public string? padChar { get; set; } // single character, default " "
}




public sealed class InputFilterSpec
{
    // For analog sources (potentiometers): ignore small changes to reduce jitter/noise.
    // Applied to the RAW numeric input value.
    public double? deadband { get; set; }

    // Minimum time between actions for this mapping (ms). Useful to prevent spamming AAO.
    public int? rateLimitMs { get; set; }
}

public sealed class InputMapSpec
{
    // Linear remap from input range to output range.
    // out = outMin + (raw - inMin) * (outMax - outMin) / (inMax - inMin)
    public double? inMin { get; set; }
    public double? inMax { get; set; }
    public double? outMin { get; set; }
    public double? outMax { get; set; }

    // "nearest" | "floor" | "ceil" | "truncate" (used for {int}/{pct} placeholders)
    public string? round { get; set; }

    // If true, clamp raw to [inMin,inMax] before mapping AND clamp out to [outMin,outMax].
    public bool? clamp { get; set; }
}

public sealed class InputMapping
{
    public string? persist { get; set; }       // optional: "laststate" (persist + replay this input state on startup)
    public string? name { get; set; }
    public string dcs { get; set; } = "";          // e.g. "PARK_BRAKE"
    public string? match { get; set; }             // e.g. "TOGGLE", "INC", "DEC", explicit numeric, or "*" (any)
    public InputFilterSpec? filter { get; set; }   // optional: deadband / rateLimit for analog jitter
    public InputMapSpec? map { get; set; }         // optional: remap raw input range to output range (for scripts/setvars)
    public AaoAction aao { get; set; } = new();    // what to send to AAO

    [JsonIgnore]
    public string SourceFile { get; set; } = "";
}
public sealed class AaoAction
{
    public string type { get; set; } = "trigger";  // trigger | setvar | script | button
    public string? name { get; set; }              // for trigger/setvar
    public double? value { get; set; }             // optional, default 1
    public string? code { get; set; }              // for script
    public int? dev { get; set; }                  // for button
    public int? chn { get; set; }
    public int? btn { get; set; }
    public int? bval { get; set; }                 // 127 pressed, 0 released
}

public static class PanelLoader
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNamingPolicy = null,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static PanelFile Load(string filePath)
    {
        var txt = File.ReadAllText(filePath);
        try
        {
            return JsonSerializer.Deserialize<PanelFile>(txt, Opt) ?? new PanelFile();
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"ERROR: Failed to parse panel file '{filePath}': {ex.Message}");
            return new PanelFile();
        }
    }
}
