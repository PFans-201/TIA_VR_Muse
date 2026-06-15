# Getting started: Muse S Athena → VR puzzle game

**Start here.** This is the complete, from-zero setup so anyone (Linux, macOS, or
Windows) can go from a fresh clone to the EEG signal driving the VR puzzle game.

It covers three ways to run it, in order of effort:

- **Mode A — Editor + PC bridge** — play the game on your PC, Muse on your head.
  *Lowest effort, works on any OS. Start here to verify everything.*
- **Mode B — Quest build + PC WiFi bridge** — the real game on the Meta Quest, with
  a PC in the loop relaying the Muse signal over WiFi.
- **Mode C — Quest standalone (direct BLE)** — the Quest talks to the Muse itself,
  no PC. *Most polished; needs one extra plugin step (§7).*

If you only want to see it work, do **§1–§4 (Mode A)** and stop.

---

## 0. What you need

**Hardware**
- A **Muse S Athena** headset (charged; the USB cable only charges — it does **not**
  carry data).
- A computer with **Bluetooth LE** (built-in or a BLE dongle).
- *For Modes B/C only:* a **Meta Quest** (2/3/Pro) in developer mode + a USB-C cable.

**Software**
- **Python 3.10+** (for the bridge in Modes A/B).
- **Unity 6000.3.11f1** via Unity Hub. *For Quest builds (B/C) you also need the
  **Android Build Support** module — see §6.*
- **Git**.

---

## 1. Clone and create the Python environment

The bridge (`Tools/muse_bridge.py`) reads the headset over BLE and does the
band-power / baseline / stress maths. It needs `numpy` + `simplepyble`.

```bash
git clone <this-repo> TIA_VR_Muse
cd TIA_VR_Muse

python -m venv .venv
# Linux/macOS:
.venv/bin/pip install -r requirements.txt
# Windows (PowerShell):
.venv\Scripts\pip install -r requirements.txt
```

> **Why a Python bridge at all?** BrainFlow (the usual EEG library) **cannot stream
> the Muse S Athena** — it connects but fails to subscribe to the data
> characteristics (an upstream bug). SimpleBLE talks to the headset perfectly, so we
> use a thin Python process instead. Full reasoning in
> [muse-unity-bridge.md](muse-unity-bridge.md).

---

## 2. Wake the headset and confirm it's seen

1. Put the Muse on / power it; the LED should be **pulsing** (= advertising). It
   sleeps when idle — if discovery fails, wiggle it or re-press the button until the
   LED pulses.
2. The bridge auto-discovers the headset by name. To just confirm it's seen, run the
   console session below (§3) and watch for a `connected to MuseS-XXXX` line, then
   `Ctrl-C`. Name-based discovery works on **Linux, macOS and Windows**.

**Linux Bluetooth gotcha:** if you get `org.bluez.Error.NotReady`, the adapter is
soft-blocked. Fix:

```bash
rfkill unblock bluetooth
sudo systemctl restart bluetooth
```

More Bluetooth troubleshooting: [eeg-debug-guide.md](eeg-debug-guide.md).

---

## 3. Take a baseline + see live stress in the terminal

Before touching Unity, confirm the whole signal path works from the terminal:

```bash
.venv/bin/python Tools/muse_bridge.py --no-udp
```

- **Phase 1 (baseline):** sit still and relaxed for ~60 s. The bridge prints
  `stress = 0.5` (neutral) while it learns your personal baseline.
- **Phase 2 (active):** it then prints **live baseline-corrected stress** (0–1) once
  per second. Concentrate hard / do mental arithmetic and watch it rise.

Useful flags: `--name MuseS-XXXX` (pick a device), `--baseline 60` (seconds),
`--window 4 --update 1` (analysis window / update rate), `--sensitivity 1.5` (gain).
On Linux/Windows you can also pin by address with `--mac <addr>`; on macOS use
`--name` (macOS hides the MAC).

If you see stress react to mental effort here, the hard part is done.

---

## 4. Mode A — run it in the Unity Editor (PC bridge)

This is the fastest way to play the real game with live EEG, on any OS. **No Android
needed.**

1. **Open the project** in Unity Hub (Unity **6000.3.11f1**).
2. **Generate the scenes** so the `MuseUdpAdapter` GameObject is created/wired:
   menu **Puzzle Game → Build All Scenes**.
3. **Start the bridge in Unity mode** (drives the two-step VR baseline from the
   tutorial — see §5):

   ```bash
   .venv/bin/python Tools/muse_bridge.py --unity
   ```

   It connects, then **waits** for the game to drive the baseline phases.
4. **Press Play** in Unity. Select the `MuseUdpAdapter` GameObject — its Inspector
   shows `streaming (active)` and the live stress once data flows. The tutorial walks
   the player through the rest + active baseline, then the game adapts difficulty/hints
   to the live stress.

That's the full loop on your PC. Everything below is about getting it onto the Quest.

---

## 5. The two-step VR baseline (important for real use)

A plain "sit still" baseline is wrong in VR: **novelty arousal** and **motor activity**
(reaching/grabbing desynchronizes the mu rhythm, which overlaps the alpha band) would
make merely *moving* look like stress. So the tutorial captures **two** baselines:

1. **Rest** — stand still (informational reference).
2. **Active-VR** — grab/move objects while *mentally relaxed* (no puzzle). This is the
   **reference** the in-game stress is measured against, so the metric captures "extra
   cognitive load on top of being-in-VR-and-moving", not the movement itself.

`TutorialManager` drives this automatically via `IMuseBaselineControl`, so it works
the same whether you're in Mode A, B, or C. Keep the active-baseline phase low-effort
or you subtract the very signal you want to detect. Details + tuning:
[muse-unity-bridge.md](muse-unity-bridge.md), [signal-tuning.md](signal-tuning.md).

---

## 6. Install Android Build Support (for any Quest build)

Needed for Modes B and C. In Unity Hub: **Installs → ⋮ on `6000.3.11f1` → Add modules
→ Android Build Support** (tick **SDK & NDK Tools** and **OpenJDK** too).

> **Linux note — if the install fails with "Installation Failed":** the Hub extracts
> the ~2 GB payload into `/tmp`, which on Arch is a small RAM-backed `tmpfs` and runs
> out of space (`errno=28: No space left on device`). Redirect the temp dir to your
> home disk and it works — **a reinstall does not help**:
>
> ```bash
> mkdir -p ~/.unity-hub-tmp
> TMPDIR="$HOME/.unity-hub-tmp" unityhub --headless install-modules \
>     --version 6000.3.11f1 --module android --childModules
> ```
> Or launch the GUI with `TMPDIR="$HOME/.unity-hub-tmp" unityhub` and add the module
> normally. Verify with:
> `ls ~/Unity/Hub/Editor/6000.3.11f1/Editor/Data/PlaybackEngines/` — you want
> `AndroidPlayer`.

**Put the Quest in developer mode:** Meta Quest mobile app → your headset → *Developer
Mode → On*. Plug in over USB-C, accept the *Allow USB debugging* prompt in-headset.
Confirm the PC sees it: `adb devices` (adb ships with the Android SDK above).

**Switch Unity to Android:** **File → Build Settings → Android → Switch Platform**.

---

## 7. Mode B — Quest build + PC WiFi bridge

The Quest runs the game but the **Muse stays connected to your PC**; the PC relays
stress to the headset over WiFi. Simplest way to get the full game on-device.

1. Put the **PC and Quest on the same WiFi/LAN**. Find both IPs (`ip addr` /
   `ipconfig` on the PC; *Settings → WiFi → your network → Advanced* on the Quest).
2. On the PC, point the bridge at the Quest:

   ```bash
   .venv/bin/python Tools/muse_bridge.py --unity --udp-host <QUEST_IP>
   ```

3. On the `MuseUdpAdapter` GameObject in the build, set **Bridge Host = `<PC_IP>`**
   (so the game's baseline-control commands reach the bridge). Leave `port` 5005 and
   `controlPort` 5006.
4. Open the PC firewall for **UDP 5006 inbound** (control) and **5005 outbound**
   (stress).
5. **File → Build Settings → Build And Run** to deploy the APK to the Quest.

---

## 8. Mode C — Quest standalone (direct BLE, no PC)

The Quest connects to the Muse itself and runs the **same validated DSP** in C#.
**This is now fully implemented** — the BLE plugin is vendored, the transport and
permissions are written, and there's a menu command to switch a scene over. You only
build and test on-device:

1. **Open the project** — Unity imports the embedded plugin
   (`Packages/com.velorexe.androidbluetoothlowenergy/`).
2. **Switch platform to Android** (§6) and put the Quest in developer mode.
3. Open `TutorialRoom`, run menu **Puzzle Game → Enable Direct BLE on Quest (current
   scene)**; repeat on `ZenPuzzleRoom`. (This adds `MuseDirectAdapter` +
   `VelorexeBleTransport` wired to the scene's `CognitiveLoadAdapter` and disables
   `MuseUdpAdapter`. Reversible.)
4. **File → Build Settings → Build And Run.** Accept the on-headset Bluetooth/Nearby
   permission prompts the first time.
5. Validate against `muse_bridge.py --no-udp` on the same session — same DSP, numbers
   should track.

Implementation details, the merge-manifest rationale, and troubleshooting are in
[muse-unity-bridge.md → *Direct BLE on Quest*](muse-unity-bridge.md).

---

## Which mode should I use?

- **Developing / demoing on a PC** → **Mode A**.
- **Need it on the Quest now, fine with a PC nearby** → **Mode B**.
- **Final, untethered headset experience** → **Mode C** (do the §7 transport step once).

---

## Reference docs

- [muse-unity-bridge.md](muse-unity-bridge.md) — bridge architecture, dual baseline,
  Quest WiFi + direct-BLE details, EEG decode reference.
- [eeg-debug-guide.md](eeg-debug-guide.md) — Bluetooth / connection troubleshooting.
- [signal-tuning.md](signal-tuning.md) — stress sensitivity, windows, band tuning.
- [brainflow-unity-setup.md](brainflow-unity-setup.md) — legacy BrainFlow path
  (Windows-only fallback).

## Common issues

| Symptom | Fix |
|---|---|
| `org.bluez.Error.NotReady` (Linux) | `rfkill unblock bluetooth && sudo systemctl restart bluetooth` |
| `Muse not found` | Wake the headset until the LED **pulses**; it sleeps when idle. |
| Android module "Installation Failed" (Linux) | Redirect `TMPDIR` to home disk — see §6. |
| Unity: `MuseUdpAdapter: listening … (no data)` | Bridge isn't running, or `--udp-port` ≠ Inspector `port`. |
| Stress stuck ~0.5 | Still in baseline phase, or poor electrode contact (TP10/ear is the usual culprit). |
