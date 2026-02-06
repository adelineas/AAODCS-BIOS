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
