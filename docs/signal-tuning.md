# Signal Tuning Guide

How to adjust the stress detection thresholds, test without a physical device, and
interpret the live Inspector values while the game is running.

## Testing without the Muse S Athena

`CognitiveLoadAdapter` has a built-in override slider so you can test the entire
adaptive difficulty pipeline in Play Mode without any EEG hardware.

1. Open the `ZenPuzzleRoom` scene and press **Play**.
2. In the Hierarchy, find the `CognitiveLoadAdapter` GameObject.
3. In the Inspector, enable **Debug Stress Override**.
4. Use the **Debug Stress Value** slider (0.0 – 1.0) to manually drive the stress signal.

All downstream systems (`SustainedStressDetector`, `AdaptiveDifficultyController`,
`PieceHintSystem`) respond to this slider exactly as they would to live EEG data.

Useful test values:

| Slider value | What it simulates |
|---|---|
| 0.0 – 0.40 | Calm — no assistance should activate |
| 0.50 | Neutral / ambiguous |
| 0.65 – 0.70 | Elevated — onset counter starts accumulating |
| 0.80+ | High stress — assistance should ramp in after ~20 s |

---

## SustainedStressDetector parameters

Find this component on the `SustainedStressDetector` GameObject in the puzzle scene.
All parameters are visible and editable in the Inspector during Play Mode.

### Onset (Calm → Stressed)

| Parameter | Default | Effect |
|---|---|---|
| `Onset Threshold` | 0.60 | Rolling mean must exceed this before the onset counter starts. Raise it to require a stronger signal before triggering. |
| `Max Variance For Onset` | 0.04 | Rolling variance must be below this. Prevents noisy bursts from counting. Std ≈ 0.2 at this value — quite stable. Raise it slightly if the signal is naturally noisier. |
| `Min Onset Seconds` | 20 | How long onset conditions must be met continuously. Raise to require a longer sustained period; lower if the game feels unresponsive. |

### Recovery (Stressed → Calm)

| Parameter | Default | Effect |
|---|---|---|
| `Recovery Threshold` | 0.40 | Mean must drop below this for recovery to begin. The 0.20 gap between onset (0.60) and recovery (0.40) is the hysteresis band — it prevents rapid toggling. |
| `Min Recovery Seconds` | 15 | How long recovery conditions must hold. Keeps assistance from disappearing the moment stress briefly dips. |

### Rolling window

| Parameter | Default | Effect |
|---|---|---|
| `Window Seconds` | 30 | Width of the rolling time window for mean and variance. Shorter = more reactive; longer = more stable but slower. |

### Live Inspector readout (read-only)

While in Play Mode these fields update in real time:

- **Rolling Mean** — current window mean. Watch this climb when load increases.
- **Rolling Variance** — current window variance. Should be < 0.04 before onset fires.
- **Onset Progress** — 0.0 to 1.0; reaches 1.0 when the detector transitions to Stressed.

---

## AdaptiveDifficultyController parameters

Find this component on the `AdaptiveDifficultyController` GameObject.

| Parameter | Default | Effect |
|---|---|---|
| `Stress Detector` | (wired) | When set, assistance only activates while `SustainedStressDetector` reports Stressed. Set to None to bypass the gate and use direct threshold mapping instead. |
| `Assistance Onset` | 0.70 | (Ungated mode only) Stress value at which mechanical assistance begins ramping. |
| `Assistance Max` | 0.95 | (Ungated mode only) Stress value at which full Easy-mode settings are applied. |
| `Ramp Up Speed` | 1.5 | Blend units per second when assistance is increasing. Higher = snappier response. |
| `Ramp Down Speed` | 0.35 | Blend units per second when assistance is fading out. Kept slow so help doesn't vanish immediately when stress briefly dips. |

**Current Blend** (read-only Inspector field) shows the current assistance blend value:
- `0.0` = no extra assistance (baseline difficulty)
- `1.0` = full Easy-mode settings (maximum magnetic snap, full ghost visibility, etc.)

---

## PieceHintSystem thresholds

The colour-hint system operates independently from the mechanical assistance above.
It activates at a lower stress threshold so hints appear before full assistance kicks in.

Find `PieceHintSystem` in the puzzle scene. The relevant field is **Hint Onset Threshold**
(default `0.55`). This reads directly from `CognitiveLoadAdapter` — it is not gated by
`SustainedStressDetector` — so hints can appear from a single elevated reading.

If hints are appearing too eagerly during normal play, raise the threshold toward 0.65.

---

## Live EEG source

**On Linux (the supported path):** EEG comes from the Python bridge, not BrainFlow.
The baseline, band-power maths and stress smoothing all happen in
`Tools/muse_bridge.py`; Unity just receives the final stress value via
`MuseUdpAdapter`. Select the **`MuseUdpAdapter`** GameObject in Play Mode to see:

| Inspector field | Meaning |
|---|---|
| `Status` | `streaming (active)` once data is flowing; `listening … (no data)` if the bridge isn't running. |
| `Stress` | The latest 0–1 stress value received from the bridge. |
| `Phase` | `baseline` (bridge is calibrating, stress pinned at 0.5) or `active`. |
| `Contact` | Whether the bridge reported good electrode contact for this reading. |

If `Status` stays `no data`:

- Make sure the bridge is running: `.venv/bin/python Tools/muse_bridge.py`.
- Confirm the ports match (`--udp-port` vs the `MuseUdpAdapter.port` Inspector field, default 5005).
- Run `Tools/muse_bridge.py --no-udp` first to confirm the device works outside Unity
  (see [eeg-debug-guide.md](eeg-debug-guide.md)).

**On Windows/macOS with BrainFlow:** the legacy `MuseAthenaAdapter` exposes
`Has Valid Data`, `Latest Band Powers`, `Smoothed Stress` and `Baseline Phase Done`
instead. On Linux that component sits idle.

---

## Recommended tuning workflow

1. **Start with the debug slider.** Set `Debug Stress Override` and sweep the slider
   from 0 to 1 slowly. Confirm:
   - Hints appear around 0.55
   - Onset counter starts around 0.60
   - After 20 s above 0.60 with low variance, `Stressed` state fires and blend ramps up
   - Dropping below 0.40 for 15 s returns to `Calm` and blend fades out

2. **Connect the device and run `muse_bridge.py --no-udp`.** Note the typical resting
   stress value for your participant. If it sits at 0.55 at rest, the system will trigger
   too easily — lower `Onset Threshold` to match the participant's true baseline.

3. **Run the full tutorial + puzzle session.** Observe `Onset Progress` in the Inspector.
   If it never reaches 1.0 even when the participant is clearly struggling, lower
   `Onset Threshold` or `Min Onset Seconds`. If it fires during VR navigation
   (before the puzzle), the active-VR baseline in TutorialRoom may need to be longer
   — increase `Active Baseline Duration` in the TutorialManager Inspector.

4. **Per-participant adjustment.** EEG band powers vary significantly between people.
   On the bridge path, pass `--sensitivity` to `muse_bridge.py` (default 1.5) — it
   scales the sigmoid, so higher values require a stronger z-score to move the stress
   value away from 0.5. If one participant's signal barely moves off 0.5 even under
   clear load, try `--sensitivity 1.0`. (On the Windows/macOS BrainFlow path the
   equivalent is the `Stress Sensitivity` field on `MuseAthenaAdapter`.)
