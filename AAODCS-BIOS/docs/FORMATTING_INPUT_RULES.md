# Input mapping rules (buttons, encoders, analog)

This bridge accepts DCS-BIOS input lines (from USB or RS-485 masters) in the form:

- `IDENTIFIER ACTION`
- `IDENTIFIER VALUE`

Examples:
- `ILS_PWR TOGGLE`
- `HDG_BUG INC`
- `TACAN_VOL 50860`

## Match tokens

`match` decides which input lines trigger an action:

- Exact match: `"1"`, `"0"`, `"TOGGLE"`, `"INC"`, `"DEC"`, `"5"`, ...
- Wildcard: `"*"` matches **any numeric value** (int/double). Intended for potentiometers/axes.

If `match` is omitted, it defaults to `"*"`.

## Analog (potentiometers / axes)

Analog inputs often jitter. Use `filter` + `map`.

### Deadband + rate limit (recommended)

```jsonc
{
  "name": "HUD_Brightness",
  "dcs": "HUD_BRIGHT",
  "match": "*",
  "filter": { "deadband": 150, "rateLimitMs": 50 },
  "map": { "inMin": 0, "inMax": 65535, "outMin": 0, "outMax": 100, "round": "nearest", "clamp": true },
  "aao": { "type": "script", "code": "{pct} (>K:HUD_BRIGHTNESS_SET)" }
}
```

### Placeholders for scripts

In `aao.code` you can use:

- `{raw}`  : raw numeric value as received
- `{value}`: mapped numeric value (after `map`), or raw if no map is configured
- `{int}`  : mapped integer (rounded according to `map.round`)
- `{pct}`  : alias for `{int}` (useful when you map to 0..100)
- `{norm}` : normalized raw value (0..1) computed from `map.inMin/inMax`

## Encoders (INC/DEC)

```jsonc
{ "dcs": "HDG_BUG", "match": "INC", "aao": { "type": "script", "code": "(>K:HEADING_BUG_INC)" } },
{ "dcs": "HDG_BUG", "match": "DEC", "aao": { "type": "script", "code": "(>K:HEADING_BUG_DEC)" } }
```

## Buttons / switches (0/1, TOGGLE)

```jsonc
{ "dcs": "TACAN_TEST_BTN", "match": "1", "aao": { "type": "script", "code": "(>K:PARKING_BRAKES)" } }
```

Notes:
- For explicit numeric matches (e.g. `match: "1"`), the bridge can do edge-detection on RS-485 (see `Inputs:EdgeNumericMatches`).


## LastState (persist + replay stateful switches)

You can opt-in per switch/axis to persist the **last numeric value** and replay it on the next bridge start.

Add to an input mapping:

- `"persist": "laststate"`

Notes:
- Only makes sense for **stateful numeric** inputs (e.g. `match: "0"`, `"1"`, `"2"`, ... or `match: "*"` for analog).
- Do **not** use for buttons/encoders like `TOGGLE`, `INC`, `DEC`.
- The cache stores `dcs -> lastValue` and is written to `input_laststate.json` (default).
- On startup, the bridge re-injects stored inputs as if they came from hardware (`IDENTIFIER VALUE`).

Example (multi-position switch, persist per DCS identifier):
```jsonc
{ "dcs": "TACAN_MODE", "match": "0", "persist": "laststate", "aao": { "type": "script", "code": "0 (>K:XYZ_SET)" } },
{ "dcs": "TACAN_MODE", "match": "1", "aao": { "type": "script", "code": "1 (>K:XYZ_SET)" } },
{ "dcs": "TACAN_MODE", "match": "2", "aao": { "type": "script", "code": "2 (>K:XYZ_SET)" } }
```

Even if only one entry has `persist:laststate`, the bridge treats it as **per DCS identifier** and will store/replay the last value for `TACAN_MODE`.
