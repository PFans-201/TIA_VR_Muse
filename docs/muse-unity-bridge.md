# Muse S Athena → Unity (UDP bridge)

How the Muse S Athena EEG signal gets into Unity on Linux.

## Why a bridge instead of BrainFlow

BrainFlow 5.22.2 (the latest) **cannot stream the Muse S Athena on Linux**. It
connects, finds the control characteristic, then fails with
`failed to notify any MuseAthena data characteristic` — an upstream bug in
BrainFlow's compiled Athena board code (see brainflow issue #776). It is **not** a
Bluetooth, firmware, or hardware problem: SimpleBLE talks to the headset perfectly
(proven — the device streams 256 Hz EEG on all four channels).

So instead of BrainFlow, a small Python process reads the headset directly and
feeds Unity:

```text
Muse S Athena ──BLE──▶ Tools/muse_bridge.py ──UDP/JSON──▶ MuseUdpAdapter ──▶ CognitiveLoadAdapter ──▶ game
   (simplepyble)        decode + baseline + stress           (Unity)            (unchanged downstream)
```

Everything downstream of `CognitiveLoadAdapter` (PieceHintSystem,
SustainedStressDetector, AdaptiveDifficultyController) is unchanged.

## One-time setup

```bash
python -m venv .venv
.venv/bin/pip install -r requirements.txt   # numpy + simplepyble
```

In Unity, regenerate the scenes once so the `MuseUdpAdapter` GameObject is added:
**Puzzle Game → Build All Scenes**.

## Running a session

1. Put on the headset (LED **pulsing** = advertising).
2. Start the bridge — it finds the headset by name, captures a baseline, then
   streams live stress:

   ```bash
   .venv/bin/python Tools/muse_bridge.py
   ```

   Name-based discovery works on **Linux, macOS and Windows**. Use `--name
   MuseS-XXXX` to pick a specific device, or `--mac <addr>` on Linux/Windows
   (macOS hides the MAC behind a random UUID, so use `--name` there).

   - During **Phase 1 (baseline)** sit still and relaxed; the bridge sends
     `stress = 0.5` so the game stays neutral.
   - During **Phase 2 (active)** it streams the live baseline-corrected stress.
   - `--baseline 60` baseline seconds, `--window 4`/`--update 1` window & latency,
     `--sensitivity 1.5` stress gain, `--no-udp` console-only,
     `--udp-port 5005` to match Unity.

3. Press **Play** in Unity. The `MuseUdpAdapter` Inspector shows
   `streaming (active)` and the live stress value once data flows.

The single baseline above (sit still) is fine for the standalone terminal test. For
the **VR game**, use the dual-baseline mode below instead.

## Dual baseline for VR (`--unity` mode)

In VR, a "sit still" baseline is contaminated by **novelty arousal** and **motor
activity** (reaching/grabbing desynchronizes the mu rhythm, which overlaps the alpha
band) — so just *moving* in VR would register as stress. The fix is a two-step
baseline, driven by Unity's `TutorialManager` so it lines up with the real tutorial:

1. **Rest baseline** — participant stands still (informational reference).
2. **Active-VR baseline** — participant grabs/moves objects while *mentally relaxed*.
   This is the **reference** the in-game stress is measured against, so the metric
   captures "extra cognitive load on top of being-in-VR-and-moving", not the movement
   itself. (Keep this phase low-effort — no puzzle — or you subtract the signal you
   want to detect.)

Run the bridge in command-driven mode:

```bash
.venv/bin/python Tools/muse_bridge.py --unity
```

It connects, then **waits** for `TutorialManager` to drive the phases. The flow:

```text
TutorialManager phase     ->  control command (UDP :5006)  ->  bridge state
RestBaseline (start/end)  ->  baseline_rest_start / _stop   ->  rest -> idle
ActiveBaseline (start)    ->  baseline_active_start         ->  active (accumulating)
ActiveBaseline (end)      ->  baseline_active_stop          ->  finalize -> streaming
```

After `baseline_active_stop` the bridge computes the per-channel baseline from the
active-VR samples and streams stress (phase `active`) for the rest of the session.
Ports: bridge→Unity stress on **5005**, Unity→bridge commands on **5006**
(`MuseUdpAdapter.port` / `controlPort`, `muse_bridge.py --udp-port` / `--control-port`).
Other commands: `reset` returns the bridge to idle to recalibrate.

## Running on a Meta Quest over WiFi (PC bridge)

The Quest is standalone Android and can't run the Python bridge itself, so for a
Quest build the bridge stays on a **PC** (with the Muse connected to the PC over BLE)
and streams to the headset over the local network. Put the PC and Quest on the **same
WiFi/LAN**, then:

1. Find both IPs (`PC_IP`, `QUEST_IP`) — e.g. `ip addr` on Linux, and on the Quest
   under *Settings → WiFi → (your network) → Advanced*.
2. On the PC, point the bridge at the Quest:

   ```bash
   .venv/bin/python Tools/muse_bridge.py --unity --udp-host <QUEST_IP>
   ```

3. On the `MuseUdpAdapter` GameObject (in the Quest build), set
   **Bridge Host = `<PC_IP>`** (so its baseline control commands reach the bridge).
   `port` 5005 and `controlPort` 5006 stay the same.
4. Allow UDP through the PC firewall: inbound **5006** (control) and outbound **5005**
   (stress). The Quest receives on 5005.

This is the simplest way to get the full game-on-Quest loop working, but it keeps a
PC in the loop. For a fully standalone headset, see *Direct BLE on Quest* below.

## Direct BLE on Quest (standalone)

To drop the PC entirely, the Quest connects to the Muse over BLE itself and runs the
same decode + DSP in C#. The hard parts are **done and validated**:

- `MuseSignalProcessor.cs` — the FFT + band powers + dual-baseline + stress, a port
  of the Python that matches it to floating-point precision (cross-checked offline).
- `MuseDirectAdapter.cs` — connects, decodes 20-byte packets (6 × 12-bit
  `(raw − 0x800) × 125/256` µV @ 256 Hz), feeds the processor, and exposes the **same
  surface as `MuseUdpAdapter`** (`StressLevel`/`Phase`, feeds `CognitiveLoadAdapter`)
  plus the same `IMuseBaselineControl` methods, so `TutorialManager` and everything
  downstream are unchanged.
- `IMuseBleTransport.cs` — the one seam between the adapter and whatever BLE plugin
  you use, so the adapter stays plugin-agnostic.

What remains is the **BLE transport** for Android/Quest. Steps:

### 1. Add a BLE plugin

Install the free [Velorexe Unity-Android-Bluetooth-Low-Energy](https://github.com/Velorexe/Unity-Android-Bluetooth-Low-Energy)
(MIT) into the project.

### 2. Android permissions

The Quest runs Android. Add to the Android manifest (Project Settings → Player →
Publishing Settings → Custom Main Manifest, or the plugin's manifest):

```xml
<uses-permission android:name="android.permission.BLUETOOTH" />
<uses-permission android:name="android.permission.BLUETOOTH_ADMIN" />
<uses-permission android:name="android.permission.BLUETOOTH_SCAN" android:usesPermissionFlags="neverForLocation" />
<uses-permission android:name="android.permission.BLUETOOTH_CONNECT" />
<uses-permission android:name="android.permission.ACCESS_FINE_LOCATION" />
```

Request the runtime permissions (`BLUETOOTH_SCAN`, `BLUETOOTH_CONNECT`, and on
older Android `ACCESS_FINE_LOCATION`) at startup before scanning.

### 3. Implement IMuseBleTransport

Write one small `MonoBehaviour` that implements `IMuseBleTransport` by calling the
plugin (verify the exact method/callback names against the installed version):

```csharp
using System;
using UnityEngine;

public class VelorexeBleTransport : MonoBehaviour, IMuseBleTransport
{
    public void StartScan(string nameContains, Action<string> onFound)
    {
        // BleManager: start scanning; on each result, if device.Name contains
        // nameContains (case-insensitive), stop scanning and onFound(device.Address).
    }
    public void Connect(string deviceId, Action onConnected, Action onDisconnected)
    {
        // BleManager.Connect(deviceId); fire onConnected on the connected callback,
        // onDisconnected on the disconnect callback.
    }
    public void Subscribe(string serviceUuid, string characteristicUuid, Action<byte[]> onData)
    {
        // Subscribe/SetNotify on (serviceUuid, characteristicUuid); forward each
        // notification's byte[] to onData. (Marshal to main thread if needed.)
    }
    public void WriteCommand(string serviceUuid, string characteristicUuid, byte[] data)
    {
        // Write without response to (serviceUuid, characteristicUuid).
    }
    public void Disconnect() { /* BleManager.Disconnect(...) */ }
}
```

### 4. Swap the adapter in the scene

On the persistent EEG GameObject (where `MuseUdpAdapter` lives), for a Quest build
use **`MuseDirectAdapter` + `VelorexeBleTransport`** instead of `MuseUdpAdapter`.
Both feed `CognitiveLoadAdapter` and both implement `IMuseBaselineControl`, so
`TutorialManager` drives the same rest + active-VR baseline either way — no other
changes. (Update `PuzzleSceneBuilder` once the transport is verified on-device.)

### Validate on the headset

Compare `MuseDirectAdapter`'s live stress against `muse_bridge.py --no-udp` on the
same session — they share the identical DSP, so the numbers should track closely.

## How it maps to the old design

| Old (BrainFlow)            | New (bridge)                                   |
|----------------------------|------------------------------------------------|
| `MuseAthenaAdapter` (C#)   | `MuseUdpAdapter` (C#) — UDP receiver           |
| in-Unity band-power DSP    | `Tools/muse_bridge.py` does the DSP            |
| TutorialManager baselines  | bridge captures the baseline (Phase 1)         |

`MuseAthenaAdapter` is left in place (it still works on Windows where BrainFlow can
stream the Athena); on Linux it sits idle and `MuseUdpAdapter` provides the data.

## EEG decode reference (for maintenance)

- EEG chars: TP9 `273e0003`, AF7 `273e0004`, AF8 `273e0005`, TP10 `273e0006`;
  control `273e0001`; service `0000fe8d-…`.
- Start sequence (length-prefixed ASCII + `\n`): `h`, `p1041`, `s`, `d`.
- Packet: 20 bytes = 2-byte sequence + 6 samples × 12-bit, decoded as
  `(raw − 0x800) × 125 / 256` µV. Sample rate 256 Hz.

## Troubleshooting

- **`Muse not found`** — wake the headset (LED must pulse); it sleeps when idle.
- **`MuseUdpAdapter: listening … (no data)`** — the bridge isn't running, or the
  ports differ (bridge `--udp-port` vs Inspector `port`).
- **Stress stuck ~0.5** — still in the baseline phase, or a flat/poor signal
  (check electrode contact; TP10/ear contact is the usual culprit).
