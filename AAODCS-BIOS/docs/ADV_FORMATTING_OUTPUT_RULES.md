# Format Cookbook (AAODCS-BIOS)

Copy/paste examples for `format` / `str` in panel outputs.

This cookbook focuses on the current model:
- `format.template` formats values (per placeholder)
- `format.round` controls rounding of numeric values
- `str` controls how the final string is fitted into the fixed-length DCS-BIOS buffer

---


## 1) Integer with fixed width

## 1) Integer with fixed width

### 1.1 `108` -> `0108` (4 digits, leading zeros)
```jsonc
"format": { "template": "{0:0000}" }
```

### 1.2 `108` -> ` 108` (right aligned in 4 characters)
```jsonc
"format": { "template": "{0,4:0}" }
```

---

## 2) Decimal values

### 2.1 `110.5` -> `110.500`
```jsonc
"format": { "template": "{0:000.000}" }
```

---

## 3) Rounding policies

### 3.1 Floor (drop decimals deterministically)
`108.99` -> `108`
```jsonc
"format": { "round": "floor", "template": "{0:0}" }
```

### 3.2 Truncate toward zero
`-1.9` -> `-1`
```jsonc
"format": { "round": "truncate", "template": "{0:0}" }
```

### 3.3 Always up (ceil)
`108.01` -> `109`
```jsonc
"format": { "round": "ceil", "template": "{0:0}" }
```

---

## 4) Compose multiple values (each formatted separately)

```jsonc
"format": { "template": "{0} {1:000.000} CRS {2:000}" }
```

- `{1:000.000}` formats value 1 with 3 decimals
- `{2:000}` formats value 2 as 3 digits

---

## 5) Fit into the display buffer (`str`)

### 5.1 Right-align the whole line inside the buffer (pad left)
```jsonc
"str": { "padSide": "left", "padChar": " ", "clipSide": "right" }
```

### 5.2 Clip behavior (if the line is too long)

Keep the left part:
```jsonc
"str": { "clipSide": "left" }
```

Keep the right part:
```jsonc
"str": { "clipSide": "right" }
```

---

## 6) Clear a display line

Send an empty string; the bridge will pad spaces up to the buffer length.

```jsonc
"sources": [ "" ],
"format": { "template": "{0}" }
```

---

## 7) Typical avionics snippets

### COM/NAV frequency as `118.275`
```jsonc
"format": { "template": "{0:000.000}" }
```

### NAV integer part (`108.75` -> `108`)
```jsonc
"format": { "round": "floor", "template": "{0:0}" }
```

### TACAN channel (1..126) as 3 digits
```jsonc
"format": { "template": "{0:000}" }
```

---

## 8) Common mistakes

### 8.1 Wrong placeholder syntax
Wrong:
```jsonc
"format": { "template": "{0000}" }
```

Correct:
```jsonc
"format": { "template": "{0:0000}" }
```

