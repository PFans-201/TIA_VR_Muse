# TIA VR Muse

A VR puzzle game that adapts its difficulty in real-time based on the player's cognitive load,
measured with a **Muse S Athena** EEG headset via BrainFlow.

## What it does

The player assembles a 3D puzzle in VR. While they play, the Muse S Athena streams EEG data
to Unity over Bluetooth. A signal processing pipeline computes a cognitive-load index
(frontal theta rise + parietal alpha drop, z-scored against a personalised resting baseline).
When the system detects sustained, stable elevation in that index, it automatically increases
assistance — magnetic snap zones strengthen, ghost outlines appear, and colour hints activate.

## Hardware requirements

- Muse S Athena EEG headset (board ID 67 in BrainFlow)
- VR headset — tested on Meta Quest via OpenXR; any OpenXR-compatible device should work
- Bluetooth 4.0+ on the host machine

## Software requirements

| Tool | Version |
|------|---------|
| Unity | 2022.3 LTS |
| XR Interaction Toolkit | 3.3.1 |
| Universal Render Pipeline | 17.3.0 |
| BrainFlow (NuGet) | 5.x |
| Python (standalone debug only) | 3.9+ with `brainflow` and `numpy` |

## Scene flow

```
EntryHall  →  TutorialRoom  →  ZenPuzzleRoom
```

| Scene | Purpose |
|-------|---------|
| **EntryHall** | Difficulty selection — the player picks their preferred level of assistance |
| **TutorialRoom** | VR controls practice and dual EEG baseline recording (see below) |
| **ZenPuzzleRoom** | The adaptive puzzle session |

### Why two baselines?

First-time VR users show elevated frontal theta from novelty arousal even before any task begins.
A single resting baseline would inflate the cognitive-load estimate for the first few minutes,
triggering false-positive stress detections.

TutorialRoom records two baselines in sequence:

1. **Rest baseline (60 s)** — stand still, eyes open, no task. Captures the absolute resting state.
2. **Active-VR baseline (60 s)** — interact with grab objects while staying mentally relaxed.
   Captures "being in VR + mild motor activity WITHOUT cognitive load."

Puzzle stress is then measured as deviation above the active-VR baseline, not above sitting-in-a-chair.
The adapter (`MuseAthenaAdapter`) persists across the scene transition carrying the recorded baselines.

## Repository structure

```
Assets/
  Editor/PuzzleGame/
    PuzzleSceneBuilder.cs        — Unity menu tool: Puzzle Game → Build All Scenes

  Scripts/PuzzleGame/
    MuseAthenaAdapter.cs         — BrainFlow streaming, baseline capture, DontDestroyOnLoad singleton
    CognitiveLoadAdapter.cs      — stress event bus used by all gameplay systems
    SustainedStressDetector.cs   — rolling-window state machine (Calm / Stressed)
    AdaptiveDifficultyController.cs — ramps assistance in/out based on detected state
    TutorialManager.cs           — dual-baseline onboarding phase controller
    PuzzleManager.cs             — puzzle state, difficulty settings, completion event
    PuzzlePiece.cs               — per-piece physics, snap detection, visual states
    MagneticSnapZone.cs          — adaptive magnetic snap force
    PieceHintSystem.cs           — colour-hint overlay system
    DifficultyLevel.cs           — enums and difficulty data structs
    DifficultyUI.cs              — UI bindings for the entry-hall difficulty picker
    ScenePortal.cs               — portal trigger for scene transitions

Tools/
  muse_athena_test.py            — standalone Python connectivity + baseline test (no Unity needed)

docs/
  eeg-debug-guide.md             — verify Muse S Athena works before opening Unity
  brainflow-unity-setup.md       — manual BrainFlow plugin install + Unity config steps
  signal-tuning.md               — adjusting thresholds, sensitivity, and testing without a device
```

## First-time setup (overview)

1. **Install BrainFlow in Unity** — follow [docs/brainflow-unity-setup.md](docs/brainflow-unity-setup.md)
2. **Verify the device** — run the Python script before putting on the headset:
   ```
   pip install brainflow numpy
   python Tools/muse_athena_test.py --mac XX:XX:XX:XX:XX:XX
   ```
   Full guide: [docs/eeg-debug-guide.md](docs/eeg-debug-guide.md)
3. **Build scenes** — in Unity: **Puzzle Game → Build All Scenes**
4. **Set MAC address** — find the `MuseAthenaAdapter` GameObject in TutorialRoom,
   enter the Muse S MAC address in the Inspector field, then enter Play Mode

For threshold tuning and testing without the device, see [docs/signal-tuning.md](docs/signal-tuning.md).
