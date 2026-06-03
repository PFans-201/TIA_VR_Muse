# EEG Standalone Debug Guide

How to verify that the Muse S Athena connects over Bluetooth and produces a valid
cognitive-load signal **without opening Unity**. Run this before any VR session to
confirm the device, the cable/BT stack, and the signal quality are all working.

## Prerequisites

```bash
pip install brainflow numpy
```

That is the only dependency. BrainFlow communicates with the headset directly over
the system Bluetooth stack — no extra drivers needed on Linux.

## Step 1 — Find the MAC address

You only need to do this once. The MAC address does not change between sessions.

**Linux (Arch, Ubuntu, etc.)**

```bash
bluetoothctl
[bluetoothctl] scan on
# Wait for a line like:
#   [NEW] Device D4:22:CD:00:AA:BB Muse-S-AB
# The XX:XX:XX:XX:XX:XX part is the MAC address you need.
[bluetoothctl] scan off
[bluetoothctl] exit
```

> If nothing appears after 30 s, try turning the headset off and on.
> The Muse S enters pairing/advertising mode automatically when powered on.
> You do NOT need to pair it in bluetoothctl — BrainFlow handles the connection itself.

**Windows**

Open **Settings → Bluetooth & devices → Add a device**.
The headset appears as "Muse-S-XXXX". Note the MAC address shown in Device Manager
under the Bluetooth entry, or use a tool like **Bluetooth LE Explorer** from the Store.

## Step 2 — Run the connectivity test

```bash
# Minimal: just check that it connects
python Tools/muse_athena_test.py --mac D4:22:CD:00:AA:BB

# Custom baseline duration (default 60 s)
python Tools/muse_athena_test.py --mac D4:22:CD:00:AA:BB --baseline 90

# Adjust stress sensitivity (see signal-tuning.md)
python Tools/muse_athena_test.py --mac D4:22:CD:00:AA:BB --sensitivity 2.0

# Run monitoring for only 60 s instead of the default 120 s
python Tools/muse_athena_test.py --mac D4:22:CD:00:AA:BB --duration 60
```

## Step 3 — Understand the output

### Phase 1: Baseline

```
============================================================
PHASE 1 — BASELINE (60s)
Sit still, relax, eyes open. Do NOT start any task yet.
============================================================

  Baseline [████████████████    ] 82%  θ=0.312  α=0.581
```

- The progress bar fills over the baseline duration.
- `θ` and `α` are the raw average band powers for theta (4–8 Hz) and alpha (8–13 Hz).
- **Good values at rest**: alpha is typically higher than theta. A ratio around 0.4–0.8 for
  theta and 0.5–1.2 for alpha is normal (exact numbers vary between people).
- If you see `[WARN] Poor signal — adjust headset`, the RMS amplitude is outside the
  expected range — adjust the headset fit and try again.

After baseline completes you will see a summary:

```
[OK] Baseline captured (28 windows)
     θ mean=0.318 ± 0.041
     α mean=0.574 ± 0.063
```

Both standard deviations should be small relative to the mean (roughly < 30% of mean).
High std means the signal was noisy or the headset moved during baseline.

### Phase 2: Active monitoring

```
 Elapsed    θ z-score   α z-score       CLI    Stress     Optical
----------------------------------------------------------------------
    2.0s       +0.21       -0.18      +0.19     0.524      3421.0
    4.0s       +0.54       -0.61      +0.58     0.562      3388.5
   ...
```

| Column | Meaning |
|--------|---------|
| `θ z-score` | How many standard deviations theta is above the resting baseline. Positive = elevated. |
| `α z-score` | How many standard deviations alpha is above the resting baseline. Negative = suppressed. |
| `CLI` | Cognitive Load Index = `(θ_z − α_z) / 2`. Positive = loaded, negative = relaxed. |
| `Stress` | `sigmoid(CLI / sensitivity)` mapped to [0, 1]. ~0.5 is neutral. |
| `Optical` | Mean absolute value of the infrared PPG channel. Confirms ancillary preset is streaming. |

### What "normal at rest" looks like

- θ z-score: near 0 (±0.3)
- α z-score: near 0 (±0.3)
- CLI: near 0 (±0.2)
- Stress: around 0.48–0.52

### What "elevated load" looks like

- θ z-score: +1.0 or higher (frontal theta rises)
- α z-score: −0.5 or lower (alpha suppressed)
- CLI: +0.5 to +1.5
- Stress: 0.60–0.80

## Troubleshooting

### "Missing dependency: No module named 'brainflow'"
```bash
pip install brainflow numpy
```

### "[ERROR] BOARD_ERROR:19" or similar BrainFlow board error

BrainFlow couldn't open the Bluetooth connection. Try:

```bash
# Arch Linux — ensure the BT daemon is running
sudo systemctl start bluetooth
rfkill unblock bluetooth

# Then try to scan again
bluetoothctl scan on
```

Also check:
- The headset is powered on (steady or blinking indicator light)
- No other device is already connected to it (BLE allows only one central connection at a time)
- The MAC address is correct — Muse S Athena typically starts with `D4:22:CD:…` or `00:55:DA:…`

### "[WARN] Poor signal — adjust headset" during baseline

- Press the four electrodes firmly against your forehead (TP9, AF7, AF8, TP10).
- Wet the electrodes slightly if contact is poor.
- Avoid large jaw movements during baseline.
- The check passes when RMS amplitude is between 0.5 µV and 800 µV.

### Optical column always shows "N/A"

The ancillary preset (infrared PPG, 64 Hz) failed to start. This is non-critical —
EEG-based cognitive load detection works without it. If you need optical data,
confirm the headset firmware is up to date via the Muse app.

### Stress value stuck at ~0.5 and never moves

The baseline captured successfully but the EEG signal is essentially flat.
Possible causes:
- Electrodes are not making contact (all channels amplifying noise only)
- The headset's reference electrode (forehead sensor) is loose
- Run `--baseline 30` with a very short baseline — if both θ std and α std are below 0.01,
  the signal is likely dead. Re-seat the headset and try again.

## What to record before the VR session

Once the script runs successfully, note down:

- The baseline θ and α means and standard deviations (printed after Phase 1)
- Any channels that consistently showed "Poor signal" warnings
- Whether optical data streamed or not

This gives you a reference to compare against the Unity Inspector values when the
`MuseAthenaAdapter` runs inside the game.
