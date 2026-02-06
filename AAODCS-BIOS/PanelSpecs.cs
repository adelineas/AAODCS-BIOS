namespace AaoDcsBiosRuntimeBridge;

public sealed class PanelSpec
{
    public string File { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public static class PanelSpecs
{
    public static List<PanelSpec> Load(Microsoft.Extensions.Configuration.IConfiguration cfg)
    {
        var list = new List<PanelSpec>();
        foreach (var ch in cfg.GetSection("Bridge:Panels").GetChildren())
        {
            var file = ch["file"] ?? "";
            var enabled = bool.TryParse(ch["enabled"], out var e) ? e : true;
            if (!string.IsNullOrWhiteSpace(file))
                list.Add(new PanelSpec { File = file, Enabled = enabled });
        }
        return list;
    }
}
