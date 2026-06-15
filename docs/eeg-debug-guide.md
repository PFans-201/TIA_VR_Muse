# EEG Standalone Debug Guide

How to verify that the Muse S Athena connects over Bluetooth and produces a valid
cognitive-load signal **without opening Unity**. Run this before any VR session to
confirm the device, the BT stack, and the signal quality are all working.

> **Why not BrainFlow?** BrainFlow 5.22.2 cannot stream the Muse S Athena on Linux
> — it connects but fails to subscribe to the data characteristics
> (`failed to notify any MuseAthena data characteristic`), an upstream bug. The
> tool below uses SimpleBLE instead, which works on Linux, macOS and Windows.
> See [muse-unity-bridge.md](muse-unity-bridge.md) for the full background.
> The old `Tools/muse_athena_test.py` (BrainFlow) is kept only for Windows/macOS
> experiments where BrainFlow's Athena path happens to work.

## Prerequisites

```bash
python -m venv .venv
.venv/bin/pip install -r requirements.txt    # simplepyble + numpy
```

No system drivers needed — SimpleBLE talks to the headset over the OS Bluetooth
stack directly.

## Step 1 — Turn on the headset

Power it on and check the **LED is pulsing** (advertising). It sleeps when idle, so
if a scan finds nothing, press the button to wake it. You do **not** need to pair it
in your OS Bluetooth settings — the bridge connects itself.

You normally do **not** need the MAC address: the tool finds the headset by name.

## Step 2 — Run the connectivity / signal test (console only)

```bash
# Find any "Muse" by name (works on Linux, macOS, Windows)
.venv/bin/python Tools/muse_bridge.py --no-udp

# Shorter baseline + a fixed-length run
.venv/bin/python Tools/muse_bridge.py --no-udp --baseline 30 --duration 60

# Pick an exact device if several are nearby
.venv/bin/python Tools/muse_bridge.py --no-udp --name MuseS-9A06

# Linux/Windows only — match by MAC (macOS hides the MAC behind a random UUID)
.venv/bin/python Tools/muse_bridge.py --no-udp --mac 00:55:DA:BB:9A:06

# More/less reactive stress (lower sensitivity = more reactive)
.venv/bin/python Tools/muse_bridge.py --no-udp --sensitivity 1.0
```

Useful flags: `--baseline <s>` (default 60), `--window <s>` band-power window
(default 4), `--update <s>` time between readings / latency (default 1),
`--sensitivity <x>` (default 1.5), `--duration <s>` (0 = until Ctrl+C),
`--verbose` to show the SimpleBLE library's internal chatter.

## Step 3 — Understand the output

### Phase 1: Baseline

```text
============================================================
PHASE 1 — BASELINE (60s)
Sit still, relax, eyes open.
============================================================
  baseline [############        ] 60%  θ=88.52 α=26.01  ch=TP9+AF7+AF8
...
[OK] Baseline channels: TP9, AF7, AF8
     TP9: θ=88.52±21.07  AF7: θ=5.23±1.79  AF8: θ=4.97±1.17
```

- The bar fills over the baseline duration. `θ`/`α` are the average theta (4–8 Hz)
  and alpha (8–13 Hz) band powers across the **good** channels.
- `ch=` shows which electrodes passed the quality check. A railing or flat
  electrode (commonly TP10 / an ear sensor) is **automatically dropped** so it
  cannot skew the baseline — seeing only 3 of 4 channels is normal.
- The baseline is captured **per channel**: each electrode is later compared
  against its own resting level, so channels dropping in/out don't distort the
  metric.

### Phase 2: Active monitoring

```text
 Elapsed      θ z      α z      CLI   Stress  ch
--------------------------------------------------------
    1.0s    -0.30    -0.06    -0.12    0.495  TP9+AF7+AF8
    2.0s    +3.54    -0.56    +2.05    0.625  TP9+AF7+AF8
```

- `θ z` — theta, in std-devs above the resting baseline (averaged over good channels)
- `α z` — alpha, in std-devs above baseline (negative = alpha suppressed)
- `CLI` — Cognitive Load Index = `(θz − αz) / 2`; positive = loaded, negative = relaxed
- `Stress` — smoothed `sigmoid(CLI / sensitivity)` in `[0,1]`; ~0.5 is neutral
- `ch` — electrodes used for this reading

**Normal at rest:** θz / αz near 0 (±0.5), CLI near 0, Stress ~0.45–0.55.
**Elevated load:** θz up (+1 or more), αz down, CLI +0.5…+1.5, Stress 0.6–0.85.

## Troubleshooting

### "Muse not found"

The headset is asleep or off. Press the button so the **LED pulses**, then retry.

### "No Bluetooth adapter found"

Turn Bluetooth on. On Linux: `rfkill unblock bluetooth && sudo systemctl start bluetooth`.

### "poor signal — adjust headset" / stress stuck ~0.5

All channels failed the quality check, or you are still in the baseline phase.
Press the electrodes (TP9, AF7, AF8, TP10) firmly to the skin; slightly dampen
them if contact is poor. The usual culprit is the TP10 / ear contact — the tool
drops it automatically, but if too many channels rail there is nothing to measure.

### A channel never appears in `ch=`

That electrode is railing (saturated, ~±1000 µV) or dead — re-seat it. The signal
still works on the remaining channels.

### Missing dependency: No module named 'simplepyble'

```bash
.venv/bin/pip install -r requirements.txt
```

## Feeding the signal into Unity

Drop the `--no-udp` flag and the same tool streams the stress value to Unity over
UDP. See [muse-unity-bridge.md](muse-unity-bridge.md).
