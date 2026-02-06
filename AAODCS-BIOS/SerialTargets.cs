namespace AaoDcsBiosRuntimeBridge;

public sealed class SerialTarget
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "usb";
    public string Port { get; set; } = "";
    public int Baud { get; set; } = 250000;
    public bool Enabled { get; set; } = true;

    // Read incoming lines from this port (DCS-BIOS text commands)
    public bool Rx { get; set; } = true;

    // Serial line signals (important for some USB-RS485/USB-serial adapters)
    public bool RtsEnable { get; set; } = true;
    public bool DtrEnable { get; set; } = false;
}

public static class SerialTargets
{
    public static List<SerialTarget> Load(Microsoft.Extensions.Configuration.IConfiguration cfg)
    {
        var list = new List<SerialTarget>();
        var section = cfg.GetSection("Outputs:Targets");
        foreach (var child in section.GetChildren())
        {
            var t = new SerialTarget
            {
                Name = child["name"] ?? "",
                Type = string.IsNullOrWhiteSpace(child["type"]) ? "usb" : (child["type"] ?? "usb"),
                Port = child["port"] ?? "",
                Baud = int.TryParse(child["baud"], out var b) ? b : 250000,
                Enabled = bool.TryParse(child["enabled"], out var e) ? e : true,
                Rx = bool.TryParse(child["rx"], out var rx) ? rx : true,
                RtsEnable = bool.TryParse(child["rtsEnable"], out var rts) ? rts : true,
                DtrEnable = bool.TryParse(child["dtrEnable"], out var dtr) ? dtr : false,
            };

            if (!string.IsNullOrWhiteSpace(t.Name) && !string.IsNullOrWhiteSpace(t.Port))
                list.Add(t);
        }
        return list;
    }
}
