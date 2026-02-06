# Formatting rules (AAODCS-BIOS)

This document describes how AAODCS-BIOS turns **values from AAO** into **fixed-length DCS-BIOS strings** for displays.

It covers **only** the bridge-side formatting. Units like `Hz/kHz/MHz`, `Bool`, `Number`, etc. come from your AAO expression in `source`.

---

## Two steps happen for every string output

### 1) Format step (what you control)
The bridge creates a string from one or more AAO values using the optional `format` object.

- If `format` is **omitted** → the raw value is emitted using **InvariantCulture** (dot as decimal separator).
- If `format` is present → the bridge can apply:
  - rounding policy (`round`) for numeric values
  - `template` rendering via `.NET string.Format(...)` (supports per-placeholder numeric formatting)

### 2) Transport step (always happens)
DCS-BIOS string exports are fixed-length buffers. After the format step produces a string, the bridge always:

- **clips** to the DCS-BIOS string length if too long
- **pads with spaces** to the DCS-BIOS string length if too short (clears leftover characters)

The buffer length is resolved from the aircraft JSON (`max_length`) and usually does not need to be configured manually.

You can override pad/clip behavior via the optional `str` object.

---

## `format` object (current model)

### Raw behavior (no `format`)
If `format` is omitted, the bridge uses:

- `value.ToString(InvariantCulture)`

Example:
- `108` → `"108"`
- `108.75` → `"108.75"`

### `format.template`
`template` uses:

- `string.Format(InvariantCulture, template, values...)`

Placeholders:
- `{0}` refers to the first value (`source`)
- `{1}` refers to the second value in `sources`, etc.

You can format each placeholder separately using standard .NET numeric format strings:

- `{0:0000}` → `108` → `0108`
- `{0:000.000}` → `110.5` → `110.500`
- `{0,4:0}` → right-aligned width 4 → `" 108"` (see `round` below for decimals)

Example: `118.275` MHz → `"118.275"`

```jsonc
{
  "dcs": "COM1_FREQ",
  "source": "(A:COM ACTIVE FREQUENCY:1, MHz)",
  "targets": ["RS485_MASTER"],
  "format": { "template": "{0:000.000}" }
}
```

### `format.round`
If you output integer-style values and must avoid rounding, set `round`.

Supported:
- `nearest` (default)
- `floor`
- `ceil`
- `truncate`

Examples:

**A) 108.75 → `108` (floor)**
```jsonc
"format": { "round": "floor", "template": "{0:0}" }
```

**B) 108.75 → ` 108` (floor + right align width 4)**
```jsonc
"format": { "round": "floor", "template": "{0,4:0}" }
```

---

## `str` object (final fit into the fixed-length buffer)

`str` is applied after `format` produced the final string.

Fields:
- `width` *(int, optional)*  
  Overrides the target width. Default: DCS-BIOS `max_length`.
- `clipSide` *(string)*: `"left"` | `"right"`  
  If too long: keep left or keep right part.
- `padSide` *(string)*: `"left"` | `"right"`  
  If too short: pad on left or on right.
- `padChar` *(string length 1)*  
  Padding character (usually `" "`).

Examples:

### Right-align the whole line in the buffer (pad left)
```jsonc
"str": { "padSide": "left", "padChar": " ", "clipSide": "right" }
```

### Left-align (default behavior)
```jsonc
"str": { "padSide": "right", "padChar": " ", "clipSide": "right" }
```

### Clear a line sample cdu
Send an empty string; the bridge will space-pad to the buffer width:
```jsonc
"sources": [ "                        " ],
"format": { "template": "{0}" }
```

---

## Supported fields (FormatSpec)

All fields are optional unless noted.

- `template` *(string)*:
  Uses `string.Format(InvariantCulture, template, values...)`.
  Mainly for multi-source compositions.

- `round` *(string)*:
  `"nearest"` | `"floor"` | `"ceil"` | `"truncate"`
  - `nearest` uses MidpointRounding.AwayFromZero.

---

## Examples (copy/paste)

### A) 108.00 MHz -> `0108` (4 digits, leading zeros)
Assume source delivers `108.00`.

```jsonc
"format": { "round": "floor", "template": "{0:0000}" }
```

### B) 108.00 MHz -> ` 108` (4 chars, right aligned)
```jsonc
"format": { "round": "floor", "template": "{0,4:0}" }
```

### C) 118.275 MHz -> `118.275`
```jsonc
"format": { "template": "{0:000.000}" }
```

### D) 12345 -> keep only last 4 digits (`2345`)
Numeric digit-clipping is not a separate feature. If you need this, format to a fixed-width string and let the normal buffer clipping apply.

```jsonc
"format": { "template": "{0:0000}" },
"str":    { "clipSide": "right" }
```

### E) Negative values (sign is preserved)
Example: `-3.2` with floor:

```jsonc
"format": { "round": "floor", "template": "{0:0}" }
```

Result: `-4`

### F) Multi-source composition using `template`
If you have multiple sources (e.g. `sources: [...]`) you can format them together.

```jsonc
"format": { "template": "{0} {1:000.000}/{2:000.000}" }
```

Notes:
- Uses `InvariantCulture`.

---

## Practical tips

### Prefer sane AAO units
Avoid exotic units like `BCD32` unless you must. Prefer:
- `Hz` / `kHz` / `MHz`
- `Number`
- `Bool`

### Decide where scaling happens
Scaling is normally best done by choosing the right AAO unit and using template formatting.
If you must apply a custom scale, do it in AAO (expression/script), then format the resulting value here.

### Remember transport padding
Even if you format `"0108"`, the bridge may append spaces up to the DCS-BIOS length (e.g. `"0108  "`).
That’s correct and necessary to clear old characters.

---

## Config validation

### Validate without running the bridge
```bash
AAODCS-BIOS.exe --check-config
```

Exit codes:
- `0` OK
- `2` config errors found

### Stop on config error during normal runs
```jsonc
"Bridge": { "StopOnConfigError": true }
```
