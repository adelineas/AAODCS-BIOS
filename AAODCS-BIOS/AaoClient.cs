using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AaoDcsBiosRuntimeBridge;

/// <summary>
/// Thin AAO WebAPI client.
/// Returns structured errors (no silent catch-all) so the caller can log meaningful diagnostics.
/// </summary>
public static class AaoClient
{
    public sealed record AaoError(
        string Kind,
        string Message,
        int? HttpStatus = null,
        string? HttpReason = null,
        string? BodySnippet = null);

    public sealed record AaoResult<T>(T? Value, AaoError? Error)
    {
        public bool Ok => Error is null;
        public static AaoResult<T> FromValue(T value) => new(value, null);
        public static AaoResult<T> FromError(AaoError error) => new(default, error);
    }

    private sealed class AaoGetVarsRequest { public AaoVarRequest[]? getvars { get; set; } }
    private sealed class AaoVarRequest { public string? @var { get; set; } public double value { get; set; } }
    private sealed class AaoGetVarsResponse { public AaoVarResponse[]? getvars { get; set; } }
    private sealed class AaoVarResponse { public string? @var { get; set; } public double value { get; set; } }

    private sealed class AaoGetStringVarsRequest { public AaoStringVarRequest[]? getstringvars { get; set; } }
    private sealed class AaoStringVarRequest { public string? @var { get; set; } public string? value { get; set; } }
    private sealed class AaoGetStringVarsResponse { public AaoStringVarResponse[]? getstringvars { get; set; } }
    private sealed class AaoStringVarResponse { public string? @var { get; set; } public string? value { get; set; } }

    private sealed class AaoActionRequest
    {
        public AaoTrigger[]? triggers { get; set; }
        public AaoSetVar[]? setvars { get; set; }
        public AaoScript[]? scripts { get; set; }
        public AaoButton[]? buttons { get; set; }
    }
    private sealed class AaoTrigger { public string? evt { get; set; } public double value { get; set; } }
    private sealed class AaoSetVar { public string? @var { get; set; } public double value { get; set; } }
    private sealed class AaoScript { public string? code { get; set; } }
    private sealed class AaoButton { public int dev { get; set; } public int chn { get; set; } public int btn { get; set; } public int bval { get; set; } }

    public static async Task<AaoResult<Dictionary<string, double>>> GetVarsBatch(
        HttpClient http,
        string endpoint,
        List<string> expressions,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct)
    {
        if (expressions.Count == 0)
            return AaoResult<Dictionary<string, double>>.FromValue(new Dictionary<string, double>(StringComparer.Ordinal));

        try
        {
            var req = new AaoGetVarsRequest
            {
                getvars = expressions.Select(e => new AaoVarRequest { @var = e, value = 0.0 }).ToArray()
            };

            using var resp = await http.PostAsJsonAsync(endpoint, req, jsonOptions, ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBody(resp, ct).ConfigureAwait(false);
                return AaoResult<Dictionary<string, double>>.FromError(new AaoError(
                    Kind: "HTTP",
                    Message: "Non-success status code",
                    HttpStatus: (int)resp.StatusCode,
                    HttpReason: resp.ReasonPhrase,
                    BodySnippet: body));
            }

            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<AaoGetVarsResponse>(text, jsonOptions);
            if (parsed?.getvars is null)
            {
                return AaoResult<Dictionary<string, double>>.FromError(new AaoError(
                    Kind: "PARSE",
                    Message: "Response JSON missing 'getvars'"));
            }

            var dict = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var v in parsed.getvars)
            {
                if (v.@var is null) continue;
                dict[v.@var] = v.value;
            }
            return AaoResult<Dictionary<string, double>>.FromValue(dict);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return AaoResult<Dictionary<string, double>>.FromError(new AaoError("TIMEOUT", ex.Message));
        }
        catch (HttpRequestException ex)
        {
            // ex.StatusCode is available in newer frameworks; keep it optional.
            return AaoResult<Dictionary<string, double>>.FromError(new AaoError("NETWORK", ex.Message));
        }
        catch (Exception ex)
        {
            return AaoResult<Dictionary<string, double>>.FromError(new AaoError("EXCEPTION", $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>
    /// Reads AAO string variables in a batch (getstringvars). This is required for sources like
    /// (L:MY_STRING, String) or (A:ATC ID, String). If AAO doesn't support the expression,
    /// it will typically return an empty string or "0".
    /// </summary>
    public static async Task<AaoResult<Dictionary<string, string>>> GetStringVarsBatch(
        HttpClient http,
        string endpoint,
        List<string> expressions,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct)
    {
        if (expressions.Count == 0)
            return AaoResult<Dictionary<string, string>>.FromValue(new Dictionary<string, string>(StringComparer.Ordinal));

        try
        {
            var req = new AaoGetStringVarsRequest
            {
                getstringvars = expressions.Select(e => new AaoStringVarRequest { @var = e, value = "" }).ToArray()
            };

            using var resp = await http.PostAsJsonAsync(endpoint, req, jsonOptions, ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBody(resp, ct).ConfigureAwait(false);
                return AaoResult<Dictionary<string, string>>.FromError(new AaoError(
                    Kind: "HTTP",
                    Message: "Non-success status code",
                    HttpStatus: (int)resp.StatusCode,
                    HttpReason: resp.ReasonPhrase,
                    BodySnippet: body));
            }

            var text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Fast parse using JsonDocument to stay tolerant across AAO builds.
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("getstringvars", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                // Try typed parse as fallback
                var parsed = JsonSerializer.Deserialize<AaoGetStringVarsResponse>(text, jsonOptions);
                if (parsed?.getstringvars is null)
                {
                    return AaoResult<Dictionary<string, string>>.FromError(new AaoError(
                        Kind: "PARSE",
                        Message: "Response JSON missing 'getstringvars'"));
                }

                var dictFallback = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var v in parsed.getstringvars)
                {
                    if (v.@var is null) continue;
                    dictFallback[v.@var] = v.value ?? string.Empty;
                }
                return AaoResult<Dictionary<string, string>>.FromValue(dictFallback);
            }

            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var el in arr.EnumerateArray())
            {
                if (!el.TryGetProperty("var", out var vnameEl) && !el.TryGetProperty("@var", out vnameEl))
                    continue;
                var vname = vnameEl.GetString();
                if (string.IsNullOrEmpty(vname))
                    continue;

                string value = string.Empty;
                if (el.TryGetProperty("value", out var vEl))
                {
                    value = vEl.ValueKind == JsonValueKind.String ? (vEl.GetString() ?? string.Empty)
                          : vEl.ValueKind == JsonValueKind.Number ? vEl.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)
                          : vEl.ToString();
                }
                dict[vname!] = value;
            }

            return AaoResult<Dictionary<string, string>>.FromValue(dict);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return AaoResult<Dictionary<string, string>>.FromError(new AaoError("TIMEOUT", ex.Message));
        }
        catch (HttpRequestException ex)
        {
            return AaoResult<Dictionary<string, string>>.FromError(new AaoError("NETWORK", ex.Message));
        }
        catch (Exception ex)
        {
            return AaoResult<Dictionary<string, string>>.FromError(new AaoError("EXCEPTION", $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    /// <summary>
    /// Sends one AAO action. On success returns an optional response body snippet (some AAO builds return JSON/text even on 200).
    /// </summary>
    public static async Task<AaoResult<string?>> SendAction(
        HttpClient http,
        string endpoint,
        InputAction action,
        JsonSerializerOptions jsonOptions,
        CancellationToken ct)
    {
        try
        {
            var req = new AaoActionRequest();

            switch (action.Kind)
            {
                case InputActionKind.Trigger:
                    req.triggers = new[] { new AaoTrigger { evt = action.Name, value = action.Value } };
                    break;
                case InputActionKind.SetVar:
                    req.setvars = new[] { new AaoSetVar { @var = action.Name, value = action.Value } };
                    break;
                case InputActionKind.Script:
                    // AAO WebAPI supports both "scripts" (RPN) and "triggers" (events+value).
                    // In practice, simple K-events are more reliable as triggers.
                    // So if the "script" is actually just a single (>K:EVENT) optionally preceded by a value,
                    // translate it to a trigger automatically.
                    {
                        var raw = (action.Code ?? string.Empty).Trim();

                        // Patterns we want to support:
                        //   "(>K:EVENT)"
                        //   "1 (>K:EVENT)"
                        //   "0 (>K:EVENT)"
                        // (We intentionally keep it strict to avoid surprising behavior.)
                        var m = System.Text.RegularExpressions.Regex.Match(
                            raw,
                            @"^(?:(?<val>[-+]?(?:\d+(?:\.\d+)?|\.\d+))\s+)?\(\s*>K:(?<evt>[^)]+)\)\s*$",
                            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

                        if (m.Success)
                        {
                            var evt = m.Groups["evt"].Value.Trim();
                            var valStr = m.Groups["val"].Success ? m.Groups["val"].Value : "1"; // default = 1
                            if (!double.TryParse(valStr, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
                                v = 1;
                            req.triggers = new[] { new AaoTrigger { evt = $"(>K:{evt})", value = v } };
                        }
                        else
                        {
                            req.scripts = new[] { new AaoScript { code = raw } };
                        }
                    }
                    break;
                case InputActionKind.Button:
                    req.buttons = new[] { new AaoButton { dev = action.Dev, chn = action.Chn, btn = action.Btn, bval = action.Bval } };
                    break;
                default:
                    return AaoResult<string?>.FromError(new AaoError("INPUT", "Unknown input action kind"));
            }

            using var resp = await http.PostAsJsonAsync(endpoint, req, jsonOptions, ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await SafeReadBody(resp, ct).ConfigureAwait(false);
                return AaoResult<string?>.FromError(new AaoError(
                    Kind: "HTTP",
                    Message: "Non-success status code",
                    HttpStatus: (int)resp.StatusCode,
                    HttpReason: resp.ReasonPhrase,
                    BodySnippet: body));
            }

            // Even on 200, AAO might return a helpful message (or an error string). Capture it for diagnostics.
            var okBody = await SafeReadBody(resp, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(okBody))
            {
                // Heuristic: treat obvious error markers as failure even if HTTP is 200.
                var b = okBody!.ToLowerInvariant();
                if (b.Contains("error") || b.Contains("exception") || b.Contains("not connected") || b.Contains("offline"))
                {
                    return AaoResult<string?>.FromError(new AaoError(
                        Kind: "AAO",
                        Message: "AAO returned an error message",
                        HttpStatus: (int)resp.StatusCode,
                        HttpReason: resp.ReasonPhrase,
                        BodySnippet: okBody));
                }
            }

            return AaoResult<string?>.FromValue(okBody);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return AaoResult<string?>.FromError(new AaoError("TIMEOUT", ex.Message));
        }
        catch (HttpRequestException ex)
        {
            return AaoResult<string?>.FromError(new AaoError("NETWORK", ex.Message));
        }
        catch (Exception ex)
        {
            return AaoResult<string?>.FromError(new AaoError("EXCEPTION", $"{ex.GetType().Name}: {ex.Message}"));
        }
    }

    private static async Task<string?> SafeReadBody(HttpResponseMessage resp, CancellationToken ct)
    {
        try
        {
            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body)) return null;

            // Keep logs short and safe.
            const int max = 300;
            body = body.Replace("\r", " ").Replace("\n", " ").Trim();
            return body.Length <= max ? body : body[..max];
        }
        catch
        {
            return null;
        }
    }
}
