using System.Text.Json;

namespace AaoDcsBiosRuntimeBridge;

public sealed class DcsCatalog
{
    public string AircraftRequested { get; init; } = "";
    public string AircraftResolved { get; init; } = "";
    public Dictionary<string, DcsControl> Controls { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DcsControl
{
    public string Identifier { get; init; } = "";
    public List<DcsOutput> Outputs { get; init; } = new();
    public List<DcsInput> Inputs { get; init; } = new();
}

public sealed class DcsOutput
{
    public string Type { get; init; } = ""; // "integer" or "string"
    public int Address { get; init; }       // decimal from JSON
    public int? Mask { get; init; }
    public int? ShiftBy { get; init; }
    public int? MaxLength { get; init; }
}

public sealed class DcsInput
{
    public string Interface { get; init; } = ""; // fixed_step, set_state, action, ...
    public int? MaxValue { get; init; }
}

public static class DcsCatalogLoader
{
    private static readonly JsonSerializerOptions Opt = new()
    {
        PropertyNamingPolicy = null,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static DcsCatalog Load(string docJsonPath, string aircraftNameOrAlias)
    {
        string aliasesPath = Path.Combine(docJsonPath, "AircraftAliases.json");
        if (!File.Exists(aliasesPath))
            throw new FileNotFoundException("AircraftAliases.json not found in doc/json path.");

        var aliasJson = File.ReadAllText(aliasesPath);

        // Supports BOTH formats:
        // 1) { "A-10C_2": ["A-10C II", ...] }  (canonical -> aliases)
        // 2) { "A-10C II": "A-10C_2", ... }    (alias -> canonical)
        Dictionary<string, string[]> aliasesCanonicalToAliases;
        Dictionary<string, string> aliasesAliasToCanonical;
        try
        {
            aliasesCanonicalToAliases = JsonSerializer.Deserialize<Dictionary<string, string[]>>(aliasJson, Opt)
                                       ?? new Dictionary<string, string[]>();
            aliasesAliasToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            aliasesCanonicalToAliases = new Dictionary<string, string[]>();
            aliasesAliasToCanonical = JsonSerializer.Deserialize<Dictionary<string, string>>(aliasJson, Opt)
                                     ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        string resolved = ResolveAircraft(docJsonPath, aircraftNameOrAlias, aliasesCanonicalToAliases, aliasesAliasToCanonical);

        var controls = new Dictionary<string, DcsControl>(StringComparer.OrdinalIgnoreCase);

        // 1) CommonData.json first (global)
        string commonPath = Path.Combine(docJsonPath, "CommonData.json");
        if (File.Exists(commonPath))
            MergeControlsFromJson(commonPath, controls);

        // 2) Aircraft json second (overrides CommonData on collisions)
        string aircraftPath = Path.Combine(docJsonPath, $"{resolved}.json");
        if (!File.Exists(aircraftPath))
            throw new FileNotFoundException($"Aircraft json not found: {aircraftPath}");

        MergeControlsFromJson(aircraftPath, controls);

        return new DcsCatalog
        {
            AircraftRequested = aircraftNameOrAlias,
            AircraftResolved = resolved,
            Controls = controls
        };
    }

    private static string ResolveAircraft(
        string docJsonPath,
        string aircraftNameOrAlias,
        Dictionary<string, string[]> canonicalToAliases,
        Dictionary<string, string> aliasToCanonical)
    {
        // direct hit
        string direct = Path.Combine(docJsonPath, $"{aircraftNameOrAlias}.json");
        if (File.Exists(direct))
            return aircraftNameOrAlias;

        // format 2) alias -> canonical
        if (aliasToCanonical.Count > 0)
        {
            if (aliasToCanonical.TryGetValue(aircraftNameOrAlias, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
                return mapped;
        }

        // format 1) canonical -> aliases
        if (canonicalToAliases.Count > 0)
        {
            if (canonicalToAliases.ContainsKey(aircraftNameOrAlias))
                return aircraftNameOrAlias;

            foreach (var kv in canonicalToAliases)
            {
                if (kv.Value.Any(a => string.Equals(a, aircraftNameOrAlias, StringComparison.OrdinalIgnoreCase)))
                    return kv.Key;
            }
        }

        // fallback: keep original (will throw later if json doesn't exist)
        return aircraftNameOrAlias;
    }

    private static void MergeControlsFromJson(string jsonPath, Dictionary<string, DcsControl> controls)
    {
        var json = File.ReadAllText(jsonPath);
        var root = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, ControlRaw>>>(json, Opt)
                   ?? throw new InvalidOperationException($"Cannot parse catalog json: {jsonPath}");

        foreach (var panel in root.Values)
        {
            foreach (var c in panel.Values)
            {
                if (string.IsNullOrWhiteSpace(c.identifier))
                    continue;

                var ctrl = new DcsControl
                {
                    Identifier = c.identifier,
                    Outputs = (c.outputs ?? new()).Select(o => new DcsOutput
                    {
                        Type = o.type ?? "",
                        Address = o.address,
                        Mask = o.mask,
                        ShiftBy = o.shift_by,
                        MaxLength = o.max_length
                    }).ToList(),
                    Inputs = (c.inputs ?? new()).Select(i => new DcsInput
                    {
                        Interface = i.@interface ?? "",
                        MaxValue = i.max_value
                    }).ToList()
                };

                // merge policy: last writer wins (aircraft overrides common)
                controls[ctrl.Identifier] = ctrl;
            }
        }
    }

    private sealed class ControlRaw
    {
        public string? identifier { get; set; }
        public List<InputRaw>? inputs { get; set; }
        public List<OutputRaw>? outputs { get; set; }
    }

    private sealed class InputRaw
    {
        public string? @interface { get; set; }
        public int? max_value { get; set; }
    }

    private sealed class OutputRaw
    {
        public string? type { get; set; } // integer/string
        public int address { get; set; }
        public int? mask { get; set; }
        public int? shift_by { get; set; }
        public int? max_length { get; set; }
    }
}