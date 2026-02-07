using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AaoDcsBiosRuntimeBridge;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    
private static string AppVersion =>
    Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion
    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
    ?? "unknown";

public static async Task<int> Main(string[] args)
{
    // Print only the version (useful for scripts/support).
    if (args.Any(a => string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(a, "-v", StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine(AppVersion);
        return 0;
    }

    int exitCode;
    try
    {
        exitCode = await MainCore(args);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("FATAL: Unhandled exception:");
        Console.Error.WriteLine(ex);
        exitCode = 2;
    }

    if (exitCode != 0 && ShouldPauseOnFatalExit(args))
    {
        PauseForUser(exitCode);
    }

    return exitCode;
}

private static bool ShouldPauseOnFatalExit(string[] args)
{
    if (args.Any(a => string.Equals(a, "--no-pause", StringComparison.OrdinalIgnoreCase)))
        return false;
    if (args.Any(a => string.Equals(a, "--pause", StringComparison.OrdinalIgnoreCase)))
        return true;

    return IsStandaloneConsoleInstance();
}

private static void PauseForUser(int exitCode)
{
    try
    {
        Console.WriteLine();
        Console.WriteLine($"ExitCode={exitCode}. Close this window or press any key to exit...");
        Console.ReadKey(intercept: true);
    }
    catch
    {
        // ignore
    }
}

private static bool IsStandaloneConsoleInstance()
{
    // Don't pause when output is redirected (e.g. piping to a file) or non-interactive.
    if (Console.IsOutputRedirected || Console.IsInputRedirected)
        return false;

    if (!OperatingSystem.IsWindows())
        return true; // best effort

    try
    {
        // When launched from Explorer (double click), usually only our process is attached to the console.
        // When launched from cmd/powershell, there are typically >=2 console processes (shell + us).
        var ids = new uint[16];
        uint count = GetConsoleProcessList(ids, (uint)ids.Length);
        return count <= 1;
    }
    catch
    {
        return true;
    }
}

[DllImport("kernel32.dll", SetLastError = true)]
private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

private static async Task<int> MainCore(string[] args)
    {
        if (args.Any(a => string.Equals(a, "--list-ports", StringComparison.OrdinalIgnoreCase)))
        {
            PortLister.PrintPorts();
            return 0;
        }

        bool checkConfigOnly = args.Any(a => string.Equals(a, "--check-config", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"AAO↔DCS-BIOS Runtime Bridge v{AppVersion}");
        Console.WriteLine();

        // IMPORTANT: SetBasePath() returns IConfigurationBuilder.
        // Keep the concrete builder instance so helper methods can work without casts.
        var cfgBuilder = new ConfigurationBuilder();
        cfgBuilder.SetBasePath(AppContext.BaseDirectory);

        AddPrimaryConfigFile(cfgBuilder);

        IConfiguration cfg = cfgBuilder
            .AddEnvironmentVariables(prefix: "ADBLB_")
            .AddCommandLine(args)
            .Build();

        // ===== AAO =====
        string? aaoBaseUrlRaw = cfg["Aao:BaseUrl"];
        if (string.IsNullOrWhiteSpace(aaoBaseUrlRaw))
        {
            Console.Error.WriteLine("CONFIG ERROR: Missing Aao:BaseUrl (e.g. http://127.0.0.1:43380/webapi/)");
            return 2;
        }

        aaoBaseUrlRaw = aaoBaseUrlRaw.Trim();
        if (!TryValidateHttpUrl(aaoBaseUrlRaw, out var aaoBaseUrlValidated, out var aaoUrlErr))
        {
            Console.Error.WriteLine($"CONFIG ERROR: Aao:BaseUrl invalid: {aaoUrlErr}");
            Console.Error.WriteLine($"CONFIG ERROR: Value was: {aaoBaseUrlRaw}");
            return 2;
        }

        string aaoEndpoint = NormalizeWebApiUrl(aaoBaseUrlValidated);
        int pollMs = ReadInt(cfg, "Aao:PollMs", 20);

        // ===== DCS-BIOS doc/json =====
        string dcsJsonPath = (cfg["DcsBios:DocJsonPath"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(dcsJsonPath) || !Directory.Exists(dcsJsonPath))
        {
            Console.Error.WriteLine("CONFIG ERROR: DcsBios:DocJsonPath missing or does not exist.");
            return 2;
        }

        // ===== Aircraft =====
        string aircraft = (cfg["DcsBios:Aircraft"] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(aircraft))
        {
            Console.Error.WriteLine("CONFIG ERROR: Missing DcsBios:Aircraft (e.g. A-10C_2)");
            return 2;
        }

        // ===== Panels =====
        string panelsDir = (cfg["Bridge:PanelsDir"] ?? "panels").Trim();
        var panelSpecs = PanelSpecs.Load(cfg);

        bool verbose = ReadBool(cfg, "Bridge:Verbose", true);
        bool ioStats = ReadBool(cfg, "Bridge:IoStats", false);
        bool logUnmatchedRx = ReadBool(cfg, "Bridge:LogUnmatchedRx", false);
        bool logAaoReply = ReadBool(cfg, "Bridge:LogAaoReply", false);
        bool edgeNumericMatches = ReadBool(cfg, "Inputs:EdgeNumericMatches", true);
        bool dryRun = ReadBool(cfg, "Bridge:DryRun", false);
        bool allowNoTargets = ReadBool(cfg, "Bridge:AllowNoTargets", false);
        bool stopOnConfigError = ReadBool(cfg, "Bridge:StopOnConfigError", false);

        var verifyExprs = ReadStringList(cfg, "Inputs:VerifyAfterAction");
        int verifyDelayMs = ReadInt(cfg, "Inputs:VerifyDelayMs", 50);

        Console.WriteLine($"AAO Endpoint      : {aaoEndpoint}");
        Console.WriteLine($"Poll (ms)         : {pollMs}");
        Console.WriteLine($"DCS-BIOS doc/json : {dcsJsonPath}");
        Console.WriteLine($"Aircraft          : {aircraft}");
        Console.WriteLine($"PanelsDir         : {panelsDir}");
        Console.WriteLine($"Panels configured : {panelSpecs.Count}");
        Console.WriteLine($"Verbose           : {verbose}");
        Console.WriteLine($"IoStats           : {ioStats}");
	    Console.WriteLine($"LogUnmatchedRx    : {logUnmatchedRx}");
	    Console.WriteLine($"LogAaoReply       : {logAaoReply}");
        Console.WriteLine($"EdgeNumericMatches: {edgeNumericMatches}");
        Console.WriteLine($"VerifyAfterAction : {verifyExprs.Count} expr(s)");
        Console.WriteLine($"VerifyDelayMs     : {verifyDelayMs}");
        Console.WriteLine($"DryRun            : {dryRun}");
        Console.WriteLine($"AllowNoTargets    : {allowNoTargets}");
        Console.WriteLine($"StopOnConfigError : {stopOnConfigError}");
        Console.WriteLine();

        // ===== Load DCS-BIOS catalog =====
        DcsCatalog catalog;
        try
        {
            catalog = DcsCatalogLoader.Load(dcsJsonPath, aircraft);
            Console.WriteLine($"DCS catalog loaded: {catalog.Controls.Count} controls (aircraft='{catalog.AircraftResolved}').");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: Cannot load DCS-BIOS catalog: {ex.Message}");
            return 2;
        }
        Console.WriteLine();

        // ===== Load panel mappings =====
        string panelsBaseDir = Path.IsPathRooted(panelsDir) ? panelsDir : Path.Combine(AppContext.BaseDirectory, panelsDir);
        if (!Directory.Exists(panelsBaseDir))
        {
            Console.Error.WriteLine($"ERROR: Panels directory not found: {panelsBaseDir}");
            return 2;
        }

        var outputs = new List<OutputMapping>();
        var inputs = new List<InputMapping>();

        foreach (var spec in panelSpecs.Where(p => p.Enabled))
        {
            var filePath = Path.Combine(panelsBaseDir, spec.File);
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"WARN : Panel file missing (skipped): {filePath}");
                continue;
            }

            try
            {
                var panel = PanelLoader.Load(filePath);
                Console.WriteLine($"Panel loaded: {spec.File} (inputs={panel.Inputs.Count}, outputs={panel.Outputs.Count})");
                foreach (var om in panel.Outputs) om.SourceFile = spec.File;
                foreach (var im in panel.Inputs) im.SourceFile = spec.File;
                outputs.AddRange(panel.Outputs);
                inputs.AddRange(panel.Inputs);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARN : Panel load failed (skipped) {spec.File}: {ex.Message}");
            }
        }

        Console.WriteLine($"Mappings: outputs={outputs.Count}, inputs={inputs.Count}");
        if (outputs.Count == 0 && inputs.Count == 0)
        {
            var enabled = string.Join(", ", panelSpecs.Where(p => p.Enabled).Select(p => p.File));
            Console.WriteLine($"WARN : No mappings loaded. Enabled panels: [{enabled}]");
            Console.WriteLine("WARN : If you only see IO stats, your panel file may be disabled or PanelsDir is wrong.");
        }
        Console.WriteLine();

        // ===== Resolve outputs against catalog =====
        var resolvedOutputs = new List<ResolvedOutputMapping>();
        int outputErrors = 0;
        foreach (var o in outputs)
        {
            if (!catalog.Controls.TryGetValue(o.Dcs, out var ctrl))
            {
                Console.WriteLine($"ERROR: {(string.IsNullOrWhiteSpace(o.SourceFile)?"<unknown>":o.SourceFile)} | output dcs='{o.Dcs}' name='{o.Name}' unknown in DCS-BIOS catalog (output skipped).\n");
                outputErrors++;
                continue;
            }

            var ro = OutputResolver.Resolve(o, ctrl);
            if (ro is null)
            {
                Console.WriteLine($"ERROR: {(string.IsNullOrWhiteSpace(o.SourceFile)?"<unknown>":o.SourceFile)} | output dcs='{o.Dcs}' name='{o.Name}' cannot be resolved (check output type/format) (output skipped).");
                outputErrors++;
                continue;
            }
            resolvedOutputs.Add(ro);
        }
        Console.WriteLine($"Resolved outputs: {resolvedOutputs.Count} (skipped={outputErrors})");
        if (outputErrors > 0)
        {
            Console.WriteLine("WARN : Some outputs were skipped due to config/catalog errors. Fix the messages above or run --check-config.");
        }
        Console.WriteLine();


// ===== Validate inputs (non-blocking by default) =====
int configErrors = outputErrors;
int skippedInputs = 0;
foreach (var im in inputs)
{
    var err = ValidateInputMapping(im);
    if (err is not null)
    {
        configErrors++;
        skippedInputs++;
        var file = string.IsNullOrWhiteSpace(im.SourceFile) ? "<unknown>" : im.SourceFile;
        Console.WriteLine($"ERROR: {file} | input dcs='{im.dcs}' name='{(string.IsNullOrWhiteSpace(im.name)?im.dcs:im.name)}' invalid: {err} (input skipped)");
    }
}
if (skippedInputs > 0)
{
    // Remove invalid ones so runtime continues cleanly.
    inputs = inputs.Where(im => ValidateInputMapping(im) is null).ToList();
    Console.WriteLine($"WARN : Skipped invalid inputs: {skippedInputs}");
    Console.WriteLine("WARN : Run with --check-config to fail fast during validation.");
    Console.WriteLine();

if (stopOnConfigError)
{
    Console.Error.WriteLine($"CONFIG ERROR: {configErrors} error(s) found. StopOnConfigError=true -> aborting.");
    return 2;
}

}

        

if (checkConfigOnly)
{
    // We already loaded catalog + panels and validated outputs/inputs resolution.
    // Serial ports are NOT opened in this mode.
    int skippedOutputs = outputs.Count - resolvedOutputs.Count;
    if (skippedOutputs > 0)
        configErrors += skippedOutputs;

    Console.WriteLine($"CHECK: resolved outputs={resolvedOutputs.Count}, total outputs={outputs.Count}, skipped outputs={skippedOutputs}");
    Console.WriteLine($"CHECK: total inputs={inputs.Count} (invalid inputs skipped earlier: {skippedInputs})");
    Console.WriteLine(configErrors == 0 ? "CHECK: OK" : $"CHECK: FAILED (errors={configErrors})");
    return configErrors == 0 ? 0 : 2;
}

// ===== Inputs =====
        var inputEngine = new InputEngine(inputs, verbose, logUnmatchedRx, edgeNumericMatches);

        // ===== Serial targets =====
        var targets = SerialTargets.Load(cfg);
        int enabledCount = targets.Count(t => t.Enabled);

        var ports = new Dictionary<string, SerialPortShell>(StringComparer.OrdinalIgnoreCase);
        var hubs = new Dictionary<string, DcsBiosHubSender>(StringComparer.OrdinalIgnoreCase);

        double frameMs = ReadDouble(cfg, "Outputs:FramePeriodMs", 33.333);
        bool keepAlive = ReadBool(cfg, "Outputs:KeepAlive:Enabled", true);
        ushort keepAddr = ReadUShort(cfg, "Outputs:KeepAlive:Address", 0xFFFE);
        ushort keepVal = ReadUShort(cfg, "Outputs:KeepAlive:Value", 0x0000);

        foreach (var t in targets.Where(t => t.Enabled))
        {
            try
            {
                var shell = new SerialPortShell(t);
                shell.Open();
                ports[t.Name] = shell;

                // Continuous export frames like DCS-BIOS
                hubs[t.Name] = new DcsBiosHubSender(shell, frameMs, keepAlive, keepAddr, keepVal);

                // RX lines (DCS-BIOS text commands)
                if (t.Rx)
                    shell.LineReceived += inputEngine.ProcessLine;

                Console.WriteLine($"Serial OPEN: {t.Name} -> {t.Port} @ {t.Baud} ({t.Type}) RX={t.Rx} RTS={t.RtsEnable} DTR={t.DtrEnable}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"WARN : Target '{t.Name}' not available on {t.Port}: {ex.Message}");
            }
        }

        if (enabledCount == 0)
            Console.WriteLine("INFO : No targets enabled (Outputs:Targets[].enabled=false).");

        if (ports.Count == 0 && !(dryRun || allowNoTargets))
        {
            Console.Error.WriteLine("ERROR: No serial ports could be opened.");
            Console.Error.WriteLine("       Set Bridge:DryRun=true to run without hardware or disable missing targets (enabled=false).");
            return 2;
        }

        Console.WriteLine();

        // ===== Shutdown =====
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            if (!cts.IsCancellationRequested)
            {
                Console.WriteLine("CTRL+C received... shutting down.");
                cts.Cancel();
            }
        };

        // Serialize AAO requests: under load AAO WebAPI can misbehave with concurrent POSTs.
        var aaoGate = new SemaphoreSlim(1, 1);

        // Build expression lists for batch polling (numeric vs string). AAO requires getstringvars
        // for expressions like (..., String).
        static bool IsAaoExpr(string s) => !string.IsNullOrWhiteSpace(s) && s.TrimStart().StartsWith("(", StringComparison.Ordinal);
        static bool IsStringExpr(string s) => IsAaoExpr(s) && s.IndexOf(", String)", StringComparison.OrdinalIgnoreCase) >= 0;

        var numericExprList = resolvedOutputs
            .SelectMany(r => r.Sources.Count > 0 ? r.Sources : new List<string> { r.Source })
            .Where(IsAaoExpr)
            .Where(s => !IsStringExpr(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var stringExprList = resolvedOutputs
            .Where(r => r.Kind == OutputKind.String && r.HasStringSources)
            .SelectMany(r => r.Sources.Count > 0 ? r.Sources : new List<string> { r.Source })
            .Where(IsStringExpr)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        using var http = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };

        AutoResetEvent? inputSignal = null;
        Thread? inputWorker = null;

        void OnActionEnqueued()
        {
            try { inputSignal?.Set(); } catch { /* ignore */ }
        }

        void CleanupSerialOnFatal()
        {
            // Close ports/hubs before we potentially pause in Main() (double-click scenario).
            try { cts.Cancel(); } catch { /* ignore */ }
            foreach (var h in hubs.Values) { try { h.Dispose(); } catch { } }
            foreach (var p in ports.Values) { try { p.Dispose(); } catch { } }
        }


        Console.WriteLine("Probe AAO once...");
        var probe = await AaoClient.GetVarsBatch(http, aaoEndpoint, numericExprList, JsonOptions, CancellationToken.None);
        if (!probe.Ok)
        {
            Console.Error.WriteLine($"ERROR: Cannot reach AAO at {aaoEndpoint}");
            Console.Error.WriteLine(FormatAaoError(probe.Error));
            Console.Error.WriteLine("Fix: Start AAO and verify Aao:BaseUrl (host, port, /webapi/). Then restart.");
            CleanupSerialOnFatal();
            return 2;
        }
        if (stringExprList.Count > 0)
        {
            var probeS = await AaoClient.GetStringVarsBatch(http, aaoEndpoint, stringExprList, JsonOptions, CancellationToken.None);
            if (!probeS.Ok)
            {
                Console.Error.WriteLine($"ERROR: Cannot read AAO string vars at {aaoEndpoint}");
                Console.Error.WriteLine(FormatAaoError(probeS.Error));
                Console.Error.WriteLine("Fix: Start AAO and verify Aao:BaseUrl (host, port, /webapi/). Then restart.");
                CleanupSerialOnFatal();
                return 2;
            }
            Console.WriteLine($"Probe OK ({probe.Value!.Count} vars, {probeS.Value!.Count} string vars)");
        }
        else
        {
            Console.WriteLine($"Probe OK ({probe.Value!.Count} vars)");
        }
        Console.WriteLine();

        // Input worker: decouple AAO actions from output polling / export streaming
        inputSignal = new AutoResetEvent(false);
        inputEngine.ActionEnqueued += OnActionEnqueued;
        inputWorker = new Thread(() => InputWorkerLoop(
            inputEngine, http, aaoEndpoint, JsonOptions, aaoGate,
            verbose, logAaoReply, dryRun, verifyExprs, verifyDelayMs,
            inputSignal, cts.Token))
        {
            IsBackground = true,
            Name = "AAO_InputWorker"
        };
        inputWorker.Start();

// Cache per target/address for BIT writes (preserve other bits)
        var wordCache = new Dictionary<(string target, ushort addr), ushort>();
        var lastByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Serial diagnostics (helps spot 'we log OUT but nothing reaches the wire')
        var lastTx = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var lastRx = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        DateTime nextIoStat = DateTime.Now.AddSeconds(2);

        int aaoFailCount = 0;
        DateTime lastAaoLog = DateTime.MinValue;
        string? lastAaoSignature = null;

        try
        {
            while (!cts.IsCancellationRequested)
            {
                // ===== OUTPUTS =====
                AaoClient.AaoResult<Dictionary<string, double>> valuesRes;
                AaoClient.AaoResult<Dictionary<string, string>> stringRes = AaoClient.AaoResult<Dictionary<string, string>>.FromValue(new Dictionary<string, string>(StringComparer.Ordinal));

                await aaoGate.WaitAsync(cts.Token);
                try
                {
                    valuesRes = await AaoClient.GetVarsBatch(http, aaoEndpoint, numericExprList, JsonOptions, cts.Token);
                    if (stringExprList.Count > 0)
                        stringRes = await AaoClient.GetStringVarsBatch(http, aaoEndpoint, stringExprList, JsonOptions, cts.Token);
                }
                finally
                {
                    aaoGate.Release();
                }

                if (!valuesRes.Ok || !stringRes.Ok)
                {
                    aaoFailCount++;
                    string sig = MakeAaoSignature(valuesRes.Ok ? stringRes.Error : valuesRes.Error);
                    var now = DateTime.Now;
                    if (sig != lastAaoSignature || (now - lastAaoLog).TotalSeconds >= 5)
                    {
                        lastAaoSignature = sig;
                        lastAaoLog = now;
                        Console.WriteLine($"{now:HH:mm:ss} WARN AAO batch read failed: {FormatAaoError(valuesRes.Ok ? stringRes.Error : valuesRes.Error)}");
                    }

                    await Task.Delay(CalcBackoffMs(aaoFailCount), cts.Token);
                    continue;
                }

                if (aaoFailCount > 0)
                {
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} INFO AAO connection restored.");
                    aaoFailCount = 0;
                    lastAaoSignature = null;
                }

                var values = valuesRes.Value!;
                var svalues = stringRes.Value!;

                foreach (var m in resolvedOutputs)
                {
                    if (m.Kind == OutputKind.Bit)
                    {
                        if (!values.TryGetValue(m.Source, out var val))
                            continue;

                        bool on = m.EvaluateBool(val);
                        string state = on ? "ON" : "OFF";

                        if (lastByName.TryGetValue(m.Name, out var prev) && prev == state)
                            continue;
                        lastByName[m.Name] = state;

                        foreach (var tgt in m.Targets)
                        {
                            if (dryRun || ports.Count == 0) continue;
                            if (!hubs.TryGetValue(tgt, out var hub)) continue;

                            var key = (tgt, m.Address);
                            wordCache.TryGetValue(key, out var curWord);
                            ushort newWord = on ? (ushort)(curWord | m.Mask) : (ushort)(curWord & ~m.Mask);
                            wordCache[key] = newWord;

                            hub.EnqueueWriteAccess(DcsBiosFrames.MakeWriteAccessU16(m.Address, newWord));
                        }

                        if (verbose)
                            Console.WriteLine($"{DateTime.Now:HH:mm:ss} OUT {m.Name}: AAO={val:0.###} -> {state}" + (dryRun ? " (DRYRUN)" : ""));
                    }
                    else if (m.Kind == OutputKind.String)
                    {
                        var srcs = m.Sources.Count > 0 ? m.Sources : new List<string> { m.Source };

                        // Build args array for template/numeric formatting. Sources that don't look like AAO expressions
                        // are treated as string literals.
                        var fmtArgs = new object[srcs.Count];
                        bool okAll = true;
                        for (int i = 0; i < srcs.Count; i++)
                        {
                            var src = srcs[i];
                            var rawSrc = src ?? string.Empty;
                            var t = rawSrc.TrimStart();

                            if (!t.StartsWith("(", StringComparison.Ordinal))
                            {
                                fmtArgs[i] = rawSrc; // literal (preserve whitespace exactly)
                                continue;
                            }

                            var expr = rawSrc.Trim();

                            if (t.IndexOf(", String)", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                if (!svalues.TryGetValue(expr, out var sv)) { okAll = false; break; }
                                fmtArgs[i] = sv ?? string.Empty;
                            }
                            else
                            {
                                if (!values.TryGetValue(t, out var vv)) { okAll = false; break; }
                                fmtArgs[i] = vv;
                            }
                        }
                        if (!okAll) continue;

                        var s = m.FormatStringArgs(fmtArgs);
                        if (s is null) continue;

                        if (lastByName.TryGetValue(m.Name, out var prev) && prev == s)
                            continue;
                        lastByName[m.Name] = s;

                        var bytes = System.Text.Encoding.Latin1.GetBytes(s);
                        foreach (var tgt in m.Targets)
                        {
                            if (dryRun || ports.Count == 0) continue;
                            if (!hubs.TryGetValue(tgt, out var hub)) continue;
                            hub.EnqueueWriteAccess(DcsBiosFrames.MakeWriteAccessBytes(m.Address, bytes));
                        }

                        if (verbose)
                            Console.WriteLine($"{DateTime.Now:HH:mm:ss} OUT {m.Name}: '{s}'" + (dryRun ? " (DRYRUN)" : ""));
                    }
                }

                // Periodic serial I/O stats
                if (ioStats && DateTime.Now >= nextIoStat && ports.Count > 0)
                {
                    nextIoStat = DateTime.Now.AddSeconds(2);
                    foreach (var kv in ports)
                    {
                        var name = kv.Key;
                        var sp = kv.Value;
                        var tx = sp.TxBytes;
                        var rx = sp.RxBytes;
                        lastTx.TryGetValue(name, out var prevTx);
                        lastRx.TryGetValue(name, out var prevRx);
                        lastTx[name] = tx;
                        lastRx[name] = rx;
                        Console.WriteLine($"{DateTime.Now:HH:mm:ss} IO  {name}: TX+{tx-prevTx}B RX+{rx-prevRx}B (total TX={tx} RX={rx})");
                    }
                }

                await Task.Delay(pollMs, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        finally
        {
            // Stop background workers before disposing resources.
            try { inputEngine.ActionEnqueued -= OnActionEnqueued; } catch { }
            try { cts.Cancel(); } catch { /* ignore */ }

            // Wake and stop input worker cleanly (prevents disposed WaitHandle crashes).
            try { inputSignal?.Set(); } catch { }
            if (inputWorker is not null)
            {
                try { inputWorker.Join(1000); } catch { }
            }
            try { inputSignal?.Dispose(); } catch { }

            foreach (var h in hubs.Values) { try { h.Dispose(); } catch { } }
            foreach (var p in ports.Values) { try { p.Dispose(); } catch { } }
        }

        return 0;
    }

    private static void InputWorkerLoop(
        InputEngine inputEngine,
        HttpClient http,
        string aaoEndpoint,
        JsonSerializerOptions jsonOptions,
        SemaphoreSlim aaoGate,
        bool verbose,
        bool logAaoReply,
        bool dryRun,
        List<string> verifyExprs,
        int verifyDelayMs,
        AutoResetEvent signal,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Wake periodically to allow shutdown
            try
            {
                signal.WaitOne(250);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            while (!ct.IsCancellationRequested && inputEngine.TryDequeue(out var act))
            {
                // Keep the log line exactly once per dequeued action
                if (verbose)
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} IN  {act.SourceLine} -> {act.AaoSummary}" + (dryRun ? " (DRYRUN)" : ""));

                if (dryRun) continue;

                try
                {
                    aaoGate.Wait(ct);
                    try
                    {
                        var res = AaoClient.SendAction(http, aaoEndpoint, act, jsonOptions, ct)
                            .GetAwaiter().GetResult();
                        if (res.Ok)
                        {
							if (verbose)
								Console.WriteLine($"{DateTime.Now:HH:mm:ss} OK  AAO action: {act.AaoSummary}");

							// If AAO returned a non-empty body, print it only when explicitly requested.
							if (verbose && logAaoReply && !string.IsNullOrWhiteSpace(res.Value))
                                Console.WriteLine($"{DateTime.Now:HH:mm:ss}      AAO reply: {res.Value}");

                            // Optional hard verification: re-read configured vars after an action.
                            if (verifyExprs.Count > 0)
                            {
                                if (verifyDelayMs > 0) Thread.Sleep(verifyDelayMs);
                                var vr = AaoClient.GetVarsBatch(http, aaoEndpoint, verifyExprs, jsonOptions, ct)
                                    .GetAwaiter().GetResult();
                                if (vr.Ok && vr.Value is not null)
                                {
                                    foreach (var e in verifyExprs)
                                    {
                                        if (vr.Value.TryGetValue(e, out var v))
                                            Console.WriteLine($"{DateTime.Now:HH:mm:ss}      VERIFY {e} = {v}");
                                    }
                                }
                                else if (!vr.Ok)
                                {
                                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} WARN VerifyAfterAction failed");
                                    Console.WriteLine($"{DateTime.Now:HH:mm:ss}      {FormatAaoError(vr.Error)}");
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine($"{DateTime.Now:HH:mm:ss} WARN AAO action failed: {act.AaoSummary}");
                            Console.WriteLine($"{DateTime.Now:HH:mm:ss}      {FormatAaoError(res.Error)}");
                        }
                    }
                    finally
                    {
                        aaoGate.Release();
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss} WARN AAO action exception: {act.AaoSummary}");
                    Console.WriteLine($"{DateTime.Now:HH:mm:ss}      {ex.Message}");
                }
            }
        }
    }

    private static int ReadInt(IConfiguration cfg, string key, int def)
        => int.TryParse(cfg[key], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

    private static bool ReadBool(IConfiguration cfg, string key, bool def)
        => bool.TryParse(cfg[key], out var v) ? v : def;

    private static List<string> ReadStringList(IConfiguration cfg, string sectionKey)
    {
        // Supports JSON arrays: "Inputs": { "VerifyAfterAction": [ "(A:...", "(A:..." ] }
        // and also comma-separated single string for convenience.
        var sec = cfg.GetSection(sectionKey);
        var children = sec.GetChildren().Select(c => (c.Value ?? "").Trim()).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (children.Count > 0) return children;

        var raw = (cfg[sectionKey] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static double ReadDouble(IConfiguration cfg, string key, double def)
        => double.TryParse(cfg[key], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

    private static ushort ReadUShort(IConfiguration cfg, string key, ushort def)
    {
        var raw = (cfg[key] ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw)) return def;

        // allow hex like 0xFFFE
        if (raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (ushort.TryParse(raw[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hx))
                return hx;
            return def;
        }

        if (ushort.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            return v;
        return def;
    }

    private static int CalcBackoffMs(int failCount)
    {
        // 200, 400, 800, 1600, 3200, 5000, 5000...
        if (failCount <= 0) return 200;
        int capped = Math.Min(failCount, 6);
        int ms = 200 * (1 << (capped - 1));
        return Math.Min(ms, 5000);
    }

    private static string MakeAaoSignature(AaoClient.AaoError? err)
        => err is null ? "(no error)" : $"{err.Kind}:{err.HttpStatus}:{err.Message}";

    private static string FormatAaoError(AaoClient.AaoError? err)
    {
        if (err is null) return "(no details)";

        var parts = new List<string> { err.Kind };
        if (err.HttpStatus is not null)
        {
            var rsn = string.IsNullOrWhiteSpace(err.HttpReason) ? "" : $" {err.HttpReason}";
            parts.Add($"HTTP {err.HttpStatus}{rsn}".Trim());
        }
        if (!string.IsNullOrWhiteSpace(err.Message))
            parts.Add(err.Message);
        if (!string.IsNullOrWhiteSpace(err.BodySnippet))
            parts.Add($"body='{err.BodySnippet}'");
        return string.Join(" | ", parts);
    }


    private static void AddPrimaryConfigFile(ConfigurationBuilder builder)
    {
        // Prefer documented JSONC config file name.
        // Fallbacks keep older setups working.
        var baseDir = AppContext.BaseDirectory;

        string[] candidates =
        {
            "AAODCS-BIOS.jsonc",
            "AAODCS-BIOS.json",
            "appsettings.jsonc",
            "appsettings.json",
        };

        string? chosen = candidates
            .Select(f => Path.Combine(baseDir, f))
            .FirstOrDefault(File.Exists);

        if (chosen is null)
        {
            Console.Error.WriteLine("CONFIG ERROR: No configuration file found.");
            Console.Error.WriteLine("Tried: AAODCS-BIOS.jsonc, AAODCS-BIOS.json, appsettings.jsonc, appsettings.json");
            Environment.Exit(2);
        }

        try
        {
            // Support JSON-with-comments (JSONC) by stripping comments before feeding to the JSON config provider.
            // This is deterministic across runtimes and does not rely on provider-specific comment behavior.
            string raw = File.ReadAllText(chosen, Encoding.UTF8);
            string stripped = StripJsonComments(raw);
            var ms = new MemoryStream(Encoding.UTF8.GetBytes(stripped));
            builder.AddJsonStream(ms);
            Console.WriteLine($"Config file        : {Path.GetFileName(chosen)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"CONFIG ERROR: Failed to load config file '{Path.GetFileName(chosen)}': {ex.Message}");
            Environment.Exit(2);
        }
    }

    private static string StripJsonComments(string input)
    {
        // Minimal JSONC -> JSON stripper:
        // - removes // line comments
        // - removes /* block comments */
        // - preserves comment markers inside strings
        var sb = new StringBuilder(input.Length);
        bool inString = false;
        bool escaped = false;

        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];

            if (inString)
            {
                sb.Append(c);
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"')
            {
                inString = true;
                sb.Append(c);
                continue;
            }

            // Line comment //
            if (c == '/' && i + 1 < input.Length && input[i + 1] == '/')
            {
                i += 2;
                while (i < input.Length && input[i] != '\n')
                    i++;
                if (i < input.Length)
                    sb.Append('\n');
                continue;
            }

            // Block comment /* ... */
            if (c == '/' && i + 1 < input.Length && input[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < input.Length && !(input[i] == '*' && input[i + 1] == '/'))
                    i++;
                i++; // skip '/'
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    

private static string? ValidateInputMapping(InputMapping im)
{
    if (im is null) return "null mapping";
    if (string.IsNullOrWhiteSpace(im.dcs)) return "missing 'dcs'";
    if (im.aao is null) return "missing 'aao'";

    // filter validation (optional)
    if (im.filter is not null)
    {
        if (im.filter.deadband is double db && db < 0) return "filter.deadband must be >= 0";
        if (im.filter.rateLimitMs is int rl && rl < 0) return "filter.rateLimitMs must be >= 0";
    }

    // map validation (optional)
    if (im.map is not null)
    {
        if (im.map.inMin is null || im.map.inMax is null || im.map.outMin is null || im.map.outMax is null)
            return "map requires inMin/inMax/outMin/outMax";
        if (Math.Abs(im.map.inMax.Value - im.map.inMin.Value) < 1e-12)
            return "map.inMin and map.inMax must not be equal";
        var r = (im.map.round ?? "nearest").Trim().ToLowerInvariant();
        if (r is not ("nearest" or "floor" or "ceil" or "truncate"))
            return "map.round must be one of: nearest|floor|ceil|truncate";
    }

    var type = (im.aao.type ?? "").Trim().ToLowerInvariant();
    if (string.IsNullOrWhiteSpace(type)) return "aao.type missing";

    switch (type)
    {
        case "script":
            if (string.IsNullOrWhiteSpace(im.aao.code)) return "aao.code required for type 'script'";
            break;
        case "trigger":
        case "setvar":
            if (string.IsNullOrWhiteSpace(im.aao.name)) return $"aao.name required for type '{type}'";
            break;
        case "button":
            if (im.aao.dev is null || im.aao.chn is null || im.aao.btn is null)
                return "aao.dev/aao.chn/aao.btn required for type 'button'";
            break;
        default:
            return $"unknown aao.type '{im.aao.type}' (expected trigger|setvar|script|button)";
    }

    // match validation (optional)
    var match = (im.match ?? "*").Trim();
    if (string.Equals(match, "", StringComparison.Ordinal)) return "match must not be empty (use '*' for wildcard)";
    // '*' is allowed; any other string is treated as exact match.

    return null;
}

private static bool TryValidateHttpUrl(string raw, out string validated, out string error)
    {
        validated = "";
        error = "";

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            error = "Not a valid absolute URL.";
            return false;
        }
        if (uri.Scheme is not ("http" or "https"))
        {
            error = "URL scheme must be http or https.";
            return false;
        }
        validated = uri.ToString();
        return true;
    }

    private static string NormalizeWebApiUrl(string baseUrl)
    {
        // AAO docs/examples typically use .../webapi/ (trailing slash).
        // Make it deterministic to avoid redirects (POST->GET problems).
        string url = baseUrl.Trim();
        if (!url.EndsWith("/", StringComparison.Ordinal))
            url += "/";

        // If user passed only host:port, append webapi/
        if (!url.EndsWith("/webapi/", StringComparison.OrdinalIgnoreCase))
        {
            if (url.EndsWith("/webapi", StringComparison.OrdinalIgnoreCase))
                url += "/";
            else
                url += "webapi/";
        }

        return url;
    }
}
