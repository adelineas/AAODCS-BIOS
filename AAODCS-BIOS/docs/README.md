# AAO ↔ DCS-BIOS Runtime Bridge

## Overview

**AAO ↔ DCS-BIOS Runtime Bridge** is a runtime translation layer that connects  
**DCS-BIOS–based hardware and data models** with **AxisAndOhs (AAO)** in order to interface with **Microsoft Flight Simulator (MSFS)**.

DCS-BIOS was originally designed to expose aircraft variables, controls, and panel data from **DCS World** to external hardware (Arduino-based panels, custom cockpits, serial devices).  
This project reuses that established **DCS-BIOS ecosystem** and bridges it into the **AAO runtime**, allowing the same hardware concepts and mappings to be used to control and drive **MSFS**.

The bridge does **not** emulate DCS World.  
DCS-BIOS is used purely as a **structural interface description**, while AAO provides the actual simulator connection.

---

## What the bridge does

- Loads **DCS-BIOS aircraft definitions** (`doc/json`)
- Automatically resolves:
  - addresses
  - masks
  - data types
  - output lengths
- Translates DCS-BIOS controls and outputs into **AAO-compatible runtime expressions**
- Acts as a **bidirectional mediator**:
  - **Outputs**  
    AAO variables → DCS-BIOS-style serial streams → hardware
  - **Inputs**  
    Hardware serial input → DCS-BIOS-style commands → AAO actions

---

## Architecture overview

```
MSFS
  ↑↓
AxisAndOhs (AAO)
  ↑↓
DCS-BIOS Runtime Bridge ↔ DCS-BIOS JSON definitions
  ↑↓
Serial hardware (Arduino, RS485, etc.)
```

---

## Configuration (JSONC, documented)

### Primary config file

The bridge is configured via **JSONC** (JSON with comments).

Preferred config file:
- `AAODCS-BIOS.jsonc`

This allows you to document configs using `//` and `/* ... */` comments.

### Optional IntelliSense / validation (VS Code / JetBrains)

This project ships JSON Schemas:

- `AAODCS-BIOS.schema.json` (main config)
- `AAODCS-BIOS.panel.schema.json` (panel files)

If your editor supports JSON schema, you get autocomplete + inline documentation.  
It is optional; Notepad users can ignore it.

---

## AAO

```jsonc
"Aao": {
  "BaseUrl": "http://localhost:43380/webapi",
  "PollMs": 20
}
```

- **BaseUrl** (required)  
  AAO WebAPI base URL.
- **PollMs**  
  Output polling interval in milliseconds.

---

## DCS-BIOS

```jsonc
"DcsBios": {
  "DocJsonPath": "dcsbios-json",
  "Aircraft": "A-10C"
}
```

- **DocJsonPath** (required)  
  Directory containing:
  - `AircraftAliases.json`
  - `CommonData.json`
  - `<Aircraft>.json` (e.g. `A-10C.json`)

---

## Outputs:Targets (Serial ports)

```jsonc
"Outputs": {
  "Targets": [
    {
      "name": "RS485_MASTER",
      "port": "COM4",
      "baud": 250000,
      "enabled": true,
      "rx": true,
      "rtsEnable": true,
      "dtrEnable": false
    },
    {
      "name": "UFC",
      "port": "COM5",
      "baud": 250000,
      "enabled": false,
      "rx": true,
	  "rtsEnable": true,
      "dtrEnable": false
    }
  ]
}
```

Each target defines a serial interface.

- **name**  
  Logical identifier referenced in panel files.
- **port**  
  Windows: `COMx`  
  Linux: `/dev/ttyACM0` or `/dev/serial/by-id/...`
- **baud**  
  Usually `250000` for DCS-BIOS.
- **enabled**  
  If false, the port is ignored.
- **rx**  
  Enables processing of incoming serial data (INPUT mappings).
- **rtsEnable / dtrEnable**  
  Serial control lines (important for some RS485 master setups).

---

## Bridge: Panel loading

```jsonc
"Bridge": {
  "PanelsDir": "panels",
  "Panels": [
    { "file": "ils.jsonc", "enabled": true },
    { "file": "tacan.jsonc", "enabled": false }
  ]
}
```

- **PanelsDir**  
  Directory containing panel definition files.
- **Panels[]**
  - **file** – panel filename
  - **enabled** – load or ignore this panel

---

## Bridge runtime flags

```jsonc
"Bridge": {
  "Verbose": false,
  "LogAaoReply": false,
  "IoStats": false,
  "DryRun": false,
  "AllowNoTargets": false,
  "StopOnConfigError": false
}
```

- **Verbose**  
  Enables detailed runtime logging (matches, IN/OUT, etc.).
- **LogAaoReply**  
  Logs AAO WebAPI replies (debug only).
- **IoStats**  
  Periodic TX/RX byte counters per serial target (debug only).
- **DryRun**  
  No serial writes are performed; outputs are logged only.
- **AllowNoTargets**  
  Allows the application to run without open serial ports.
- **StopOnConfigError**  
  If true: exit on config errors.  
  Default false: invalid mappings are logged and skipped; the bridge continues.

---

## Panel file format (JSONC)

Each panel file contains **both outputs and inputs**.

```jsonc
{
  "outputs": [ ... ],
  "inputs":  [ ... ]
}
```

---

## Output mapping

### Bit output example (LED)

```jsonc
{
  "dcs": "MASTER_CAUTION",
  "source": "(A:BRAKE PARKING POSITION, Bool)",
  "targets": ["RS485_MASTER"],
  "threshold": "0.5",
  "invert": false
}
```

**Fields:**
- **dcs** (required)  
  DCS-BIOS output name.
- **source** (required)  
  AAO expression.
- **targets** (required)  
  Serial targets.
- **threshold** (optional)  
  For bit outputs.
- **invert** (optional)

Addresses, masks, and lengths are resolved automatically from DCS-BIOS JSON.

---

### Output formatting model

String outputs use two independent parts:

1) `format` — convert one or more AAO values into a string (template + optional rounding)
2) `str` — fit the resulting string into the DCS-BIOS buffer (pad/clip/optional width override)

#### 1) `format.template` (recommended)

`format.template` uses `.NET string.Format` with invariant culture (decimal dot).

You can format each placeholder separately:

- `{0:000.000}` → always 3 decimals (e.g. `110.5` → `110.500`)
- `{0:0000}`    → leading zeros (e.g. `108` → `0108`)
- `{0,4:0}`     → right-aligned in 4 characters (e.g. `108` → `" 108"`)

Example: COM1 active frequency as `118.275`

```jsonc
{
  "dcs": "COM1_FREQ",
  "source": "(A:COM ACTIVE FREQUENCY:1, MHz)",
  "targets": ["RS485_MASTER"],
  "format": { "template": "{0:000.000}" }
}
```

Example: compose a label + a formatted number

```jsonc
{
  "dcs": "CDU_LINE5",
  "sources": [
    "NAV1:",
    "(A:NAV ACTIVE FREQUENCY:1, MHz)"
  ],
  "targets": ["RS485_MASTER"],
  "format": { "template": "{0} {1:000.000}" }
}
```

#### 2) `format.round` (floor/ceil/truncate/nearest)

If you need deterministic integer behavior (e.g. **floor** instead of rounding), set:

```jsonc
"format": {
  "round": "floor",
  "template": "{0,4:0}"
}
```

Supported:
- `nearest` (default)
- `floor`
- `ceil`
- `truncate`

#### 3) `str` (pad/clip + optional width override)

`str` is applied after `format.template` produced the final text.

Common use: right-align the whole line inside the buffer:

```jsonc
"str": {
  "padSide": "left",
  "padChar": " ",
  "clipSide": "right"
}
```

Optional: override the buffer width (normally not needed — defaults to DCS-BIOS `max_length`):

```jsonc
"str": { "width": 24 }
```

For full rules + examples see:
- `FORMATTING_OUTPUT_RULES.md`
- `ADV_FORMATTING_OUTPUT_RULES.md`

---

## Input mapping

### Digital input (button/switch)

```jsonc
{
  "dcs": "ILS_PWR",
  "match": "TOGGLE",
  "aao": {
    "type": "script",
    "code": "(>K:NAV1_RADIO_SWAP)"
  }
}
```

- **dcs** (required)  
  First token of incoming serial line.
- **match** (optional)  
  Second token (`1`, `0`, `TOGGLE`, `INC`, `DEC`, `*`).
- **aao** (required)  
  Action sent to AAO:
  - `trigger`
  - `setvar`
  - `script`
  - `button`

---

## Analog inputs (potentiometer/axis) + mapping/filtering

For analog controls (potentiometers), DCS-BIOS sends numeric values (often 0..65535).  
Use `match: "*"` to accept any numeric value, then optionally apply filtering and mapping.

Example: 0..65535 → 0..100 (%), then inject into AAO script:

```jsonc
{
  "name": "DisplayBrightness",
  "dcs": "TACAN_VOL",
  "match": "*",
  "filter": { "deadband": 150, "rateLimitMs": 50 },
  "map": { "inMin": 0, "inMax": 65535, "outMin": 0, "outMax": 100, "round": "nearest", "clamp": true },
  "aao": {
    "type": "script",
    "code": "{pct} (>K:YOUR_BRIGHTNESS_SET_EVENT)"
  }
}
```

### Script placeholders for analog mappings

When `match:"*"` is used, the bridge can inject values into `aao.code`:

- `{raw}` → raw numeric input (as received)
- `{value}` → mapped output value (after `map`, else raw)
- `{int}` → mapped integer value
- `{pct}` → alias of `{int}` (useful when mapping to 0..100)
- `{norm}` → normalized 0..1 (from `map.inMin/inMax`)

---

## Debugging / tools

### Validate configuration without running sim/hardware

```bash
AAODCS-BIOS --check-config
```

Exit codes:
- `0` OK
- `2` config errors found

---

## Downloads

- **Framework-dependent**: requires **.NET 8 runtime** installed on the target system.

If you run the framework-dependent build without .NET 8 installed, Windows will show a prompt with a download link to install .NET.

---

## License

MIT License.  
Free to use, modify, and redistribute.
