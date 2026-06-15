# TIA VR Muse

A VR puzzle game that adapts its difficulty in real time to the player's cognitive
load, measured with a **Muse S Athena** EEG headset.

## What it does

The player assembles a 3D puzzle in VR. While they play, the Muse S Athena streams EEG
data, and a signal-processing pipeline computes a cognitive-load index (frontal theta
rise + parietal alpha drop, z-scored against a personalised baseline). When that index
stays elevated, the game quietly increases assistance — magnetic snap zones strengthen,
ghost outlines appear, and colour hints activate.

## How the EEG signal gets in

BrainFlow can't stream the Muse S Athena on Linux, so the project reads the headset
with a small SimpleBLE process and exposes the stress signal three ways (pick one):

| Mode | Path | Use it for |
|------|------|------------|
| **A** | Muse → PC Python bridge → Unity **Editor** (UDP) | Developing / demoing on a PC |
| **B** | Game on **Quest**, Muse on PC, stress relayed over WiFi | Quest build with a PC nearby |
| **C** | **Quest** talks to the Muse over BLE itself (no PC) | Final untethered headset |

Everything downstream of `CognitiveLoadAdapter` is identical across all three modes.
The full reasoning is in [docs/muse-unity-bridge.md](docs/muse-unity-bridge.md).

## ▶ Getting started

**New here? Follow [docs/getting-started.md](docs/getting-started.md)** — the complete
from-zero setup (Python bridge, headset, Unity, and all three modes) for Linux, macOS,
and Windows. The short version:

```bash
python -m venv .venv && .venv/bin/pip install -r requirements.txt
.venv/bin/python Tools/muse_bridge.py --no-udp   # baseline + live stress in the terminal
```

Then in Unity (**6000.3.11f1**): **Puzzle Game → Build All Scenes**, run
`Tools/muse_bridge.py --unity`, and press **Play**.

## Requirements

- **Muse S Athena** EEG headset (the USB cable only charges — data is over BLE)
- A machine with **Bluetooth LE**
- **Unity 6000.3.11f1** (Android Build Support for Quest builds — modes B/C)
- **Python 3.10+** for the bridge (`numpy`, `simplepyble` — see `requirements.txt`)
- VR: **Meta Quest** via OpenXR (XR Interaction Toolkit 3.3.1, URP 17.3.0)

## Scene flow

```text
EntryHall  →  TutorialRoom  →  ZenPuzzleRoom
```

| Scene | Purpose |
|-------|---------|
| **EntryHall** | Difficulty selection — the player picks their level of assistance |
| **TutorialRoom** | VR controls practice + dual EEG baseline recording (below) |
| **ZenPuzzleRoom** | The adaptive puzzle session |

### Why two baselines?

In VR a plain "sit still" baseline is contaminated by **novelty arousal** and **motor
activity** (reaching/grabbing desynchronises the mu rhythm, which overlaps alpha), so
merely *moving* would look like stress. TutorialRoom records two baselines in sequence:

1. **Rest (60 s)** — stand still, no task — the absolute resting state.
2. **Active-VR (60 s)** — grab/move objects while mentally relaxed — "being in VR and
   moving, *without* cognitive load."

Puzzle stress is measured as deviation above the **active-VR** baseline. The methodology
and tuning live in [docs/muse-unity-bridge.md](docs/muse-unity-bridge.md) and
[docs/signal-tuning.md](docs/signal-tuning.md).

## Repository map

```text
Assets/
  Editor/PuzzleGame/PuzzleSceneBuilder.cs   — menu: Puzzle Game → Build All Scenes
  Scripts/PuzzleGame/
    CognitiveLoadAdapter.cs        — stress event bus all gameplay systems listen to
    MuseUdpAdapter.cs              — receives stress from the Python bridge (modes A/B)
    MuseDirectAdapter.cs           — on-device Muse BLE + DSP for standalone Quest (mode C)
    VelorexeBleTransport.cs        — Quest BLE transport (IMuseBleTransport impl)
    MuseSignalProcessor.cs         — in-Unity FFT/band-power/baseline/stress (mode C)
    IMuseBleTransport.cs · IMuseBaselineControl.cs — plugin/baseline seams
    SustainedStressDetector.cs · AdaptiveDifficultyController.cs — detect & ramp assistance
    TutorialManager.cs             — dual-baseline onboarding controller
    PuzzleManager.cs · PuzzlePiece.cs · MagneticSnapZone.cs · PieceHintSystem.cs
    DifficultyLevel.cs · DifficultyUI.cs · ScenePortal.cs
Tools/
  muse_bridge.py                   — SimpleBLE bridge: decode + baseline + stress → UDP
Packages/
  com.velorexe.androidbluetoothlowenergy/ — vendored Quest BLE plugin (mode C)
docs/
  getting-started.md               — ▶ start here: full from-zero setup, all modes
  muse-unity-bridge.md             — bridge architecture, dual baseline, Quest direct BLE
  eeg-debug-guide.md               — Bluetooth / connection troubleshooting
  signal-tuning.md                 — thresholds, sensitivity, testing without a device
  brainflow-unity-setup.md         — legacy BrainFlow path (Windows-only fallback)
```

> `MuseAthenaAdapter.cs` (the original BrainFlow adapter) is retained as a Windows-only
> fallback; on Linux/macOS the bridge supersedes it. See
> [docs/brainflow-unity-setup.md](docs/brainflow-unity-setup.md).
