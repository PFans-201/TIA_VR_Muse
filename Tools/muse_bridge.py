"""
Muse S Athena -> Unity bridge (simplepyble)  —  cross-platform (Linux / macOS / Windows)
========================================================================================

WHY THIS EXISTS
    BrainFlow 5.22.2 cannot stream the Muse S Athena on Linux: it connects but
    fails to subscribe to the data characteristics ("failed to notify any
    MuseAthena data characteristic"), an upstream bug in BrainFlow's compiled
    Athena board code. SimpleBLE (via simplepyble) talks to the headset fine on
    every OS, so this script replaces the BrainFlow data path entirely. It:
      1. Connects to the Muse over BLE and starts the EEG stream itself.
      2. Decodes the raw 12-bit EEG packets into microvolts (4 channels @ 256 Hz),
         dropping any electrode that is railing / has poor contact.
      3. Computes a baseline-corrected cognitive-load / stress signal. Each channel
         is z-scored against ITS OWN resting baseline and the z-scores are averaged,
         so a channel dropping in/out does not distort the metric.
      4. Optionally streams the result as JSON-per-line over UDP for Unity
         (see Assets/Scripts/PuzzleGame/MuseUdpAdapter.cs).

DEVICE SELECTION (cross-platform)
    By default it finds the headset BY NAME ("Muse"), which works on every OS.
    On macOS the OS hides the MAC address (it reports a random UUID instead), so
    --mac only works on Linux/Windows; prefer --name everywhere.

USAGE
    python Tools/muse_bridge.py                       # find any "Muse", console + UDP
    python Tools/muse_bridge.py --no-udp              # console only (terminal EEG test)
    python Tools/muse_bridge.py --name MuseS-9A06     # exact device by name
    python Tools/muse_bridge.py --mac 00:55:DA:BB:9A:06   # Linux/Windows only
    python Tools/muse_bridge.py --baseline 60 --window 4 --update 1 --sensitivity 1.5

DEPENDENCIES
    pip install simplepyble numpy
"""

import argparse
import collections
import json
import math
import os
import socket
import subprocess
import sys
import threading
import time

# Mute SimpleBLE's native stderr chatter (the "experimental new Bluez backend"
# warning and async D-Bus notices it emits from a C++ background thread) as early
# as possible — before importing it — exactly like a shell `2>/dev/null`. The
# original stderr fd is saved so main() can restore it around argument parsing
# (keeping argparse errors visible) and re-mute for the session. Skip with --verbose.
_SAVED_STDERR_FD = None
if "--verbose" not in sys.argv:
    _SAVED_STDERR_FD = os.dup(2)
    _dn = os.open(os.devnull, os.O_WRONLY)
    os.dup2(_dn, 2)
    os.close(_dn)

try:
    import numpy as np
except ImportError as e:
    print(f"[ERROR] Missing dependency: {e}")
    print("Run:  pip install simplepyble numpy")
    sys.exit(1)

# simplepyble is imported lazily in main() AFTER stderr is muted, so the library's
# import-time "experimental new Bluez backend" chatter is suppressed too.
simplepyble = None


def _load_simplepyble():
    global simplepyble
    try:
        import simplepyble as _sble
    except ImportError as e:
        print(f"[ERROR] Missing dependency: {e}")
        print("Run:  pip install simplepyble numpy")
        sys.exit(1)
    simplepyble = _sble

# ── Muse GATT layout (model-specific, OS-independent) ─────────────────────────
SERVICE = "0000fe8d-0000-1000-8000-00805f9b34fb"
CTRL    = "273e0001-4c4d-454d-96be-f03bac821358"
EEG_CHARS = [  # order = channel index; TP9, AF7, AF8, TP10
    ("273e0003-4c4d-454d-96be-f03bac821358", "TP9"),
    ("273e0004-4c4d-454d-96be-f03bac821358", "AF7"),
    ("273e0005-4c4d-454d-96be-f03bac821358", "AF8"),
    ("273e0006-4c4d-454d-96be-f03bac821358", "TP10"),
]
EEG_NAMES   = [name for _, name in EEG_CHARS]
SAMPLE_RATE = 256                       # Hz, measured
START_CMDS  = ("h", "p1041", "s", "d")  # halt, Athena preset, status, start

# ── Signal-processing constants ───────────────────────────────────────────────
RMS_MIN, RMS_MAX = 0.5, 800.0
RAIL_UV = 990.0      # near the 12-bit saturation limit (~±1000 µV) => bad contact
SMOOTH_ALPHA = 0.25
DELTA, THETA, ALPHA, BETA, GAMMA = 0, 1, 2, 3, 4  # indices into the band vector
BAND_NAMES = ["delta", "theta", "alpha", "beta", "gamma"]
# [delta, theta, alpha, beta, gamma]
BANDS = [(1.0, 4.0), (4.0, 8.0), (8.0, 13.0), (13.0, 30.0), (30.0, 45.0)]


def mute_native_stderr():
    """Permanently point fd 2 at /dev/null so the SimpleBLE native library's
    chatter (the "experimental new Bluez backend" warning and async D-Bus notices,
    which fire from a background thread at unpredictable times — including during
    interpreter shutdown) never reaches the terminal. Real Python errors are still
    shown because main() catches them and prints to stdout (fd 1)."""
    devnull = os.open(os.devnull, os.O_WRONLY)
    os.dup2(devnull, 2)
    os.close(devnull)


# ── DSP ───────────────────────────────────────────────────────────────────────
def _channel_band_powers(x, fs):
    """One channel (µV) -> [delta,theta,alpha,beta,gamma] absolute band powers."""
    n = len(x)
    freqs = np.fft.rfftfreq(n, d=1.0 / fs)
    hann = np.hanning(n)
    win_power = np.sum(hann ** 2)
    x = x - np.mean(x)                       # detrend (kills DC / drift)
    spec = np.fft.rfft(x * hann)
    psd = (np.abs(spec) ** 2) / (fs * win_power)
    psd[1:-1] *= 2.0                         # one-sided
    return np.array([np.sum(psd[(freqs >= lo) & (freqs < hi)]) * (fs / n)
                     for (lo, hi) in BANDS])


def per_channel_band_powers(window, fs):
    """window: (channels, samples) µV -> {name: band-power vector} for GOOD
    channels only. A channel is dropped if it is railing (saturated contact),
    flat/dead, or produces non-finite powers."""
    out = {}
    for ch, name in zip(window, EEG_NAMES):
        rail_frac = float(np.mean(np.abs(ch) >= RAIL_UV))
        rms = float(np.sqrt(np.mean(ch ** 2)))
        if rail_frac >= 0.10 or not (RMS_MIN <= rms <= RMS_MAX):
            continue
        bp = _channel_band_powers(ch, fs)
        if np.all(np.isfinite(bp)):
            out[name] = bp
    return out


def sigmoid(x):
    return 1.0 / (1.0 + math.exp(-x))


def finalize_baseline(samples):
    """{name: [band vectors]} -> {name: (mean, std)} for channels seen in at least
    half the windows. Empty if no channel produced a stable baseline."""
    n = max((len(v) for v in samples.values()), default=0)
    return {name: (np.mean(v, axis=0), np.std(v, axis=0))
            for name, v in samples.items() if len(v) >= max(3, n // 2)}


def band_zscores(bp, base):
    """Per-channel z-scores of every band vs the baseline, averaged over the channels
    present in both `bp` and `base`. Returns (z_vector[5], used_channel_names) where
    z_vector is [delta_z, theta_z, alpha_z, beta_z, gamma_z], or None if no overlap."""
    rows, used = [], []
    for name, vec in bp.items():
        if name not in base:
            continue
        m, s = base[name]
        z = [(vec[b] - m[b]) / s[b] if s[b] > 1e-9 else 0.0 for b in range(len(vec))]
        rows.append(z)
        used.append(name)
    if not used:
        return None
    return list(np.mean(rows, axis=0)), used


def indices_from_z(z, sensitivity):
    """Map averaged band z-scores -> three 0..1 cognitive indices, each squashed with the
    same sigmoid(raw / sensitivity) so they share one axis. All are measured ABOVE the
    committed (active-VR) baseline, i.e. 0.5 == "same as baseline".
        cognitive_load : (theta_z - alpha_z)/2   frontal theta up, alpha down  (== legacy "stress")
        attention      : (beta_z - theta_z)/2    inverse Theta/Beta Ratio — a LOW theta/beta ratio
                                                 (theta down, beta up) means focused attention, so
                                                 this index rises as the TBR falls.
        stress         : (beta_z - alpha_z)/2    beta/arousal up, alpha down
    Returns a dict of the three RAW (un-smoothed) 0..1 values plus the contributing z-scores."""
    theta_z, alpha_z, beta_z = z[THETA], z[ALPHA], z[BETA]
    cli   = (theta_z - alpha_z) / 2.0
    focus = (beta_z - theta_z) / 2.0          # inverse theta/beta ratio (TBR) — high = focused
    arous = (beta_z - alpha_z) / 2.0
    return {
        "cognitive_load": sigmoid(cli   / sensitivity),
        "attention":      sigmoid(focus / sensitivity),
        "stress":         sigmoid(arous / sensitivity),
        "theta_z": theta_z, "alpha_z": alpha_z, "beta_z": beta_z, "cli": cli,
    }


class IndexSmoother:
    """Peak-biased smoothing for the three 0..1 indices (each starts neutral at 0.5).

    The cognitive signals are mostly TRANSIENT — short bursts of stress / cognitive load rather
    than sustained plateaus. A symmetric EMA buried those peaks (it averaged each spike back down
    toward the mean), so in-game reactivity felt flat. Instead this uses an asymmetric envelope
    follower: a FAST attack so the index jumps up to a peak almost immediately, and a SLOW release
    so it eases back down — emphasising peaks while still filtering jitter. Set attack==release for
    the old symmetric behaviour."""
    KEYS = ("stress", "attention", "cognitive_load")

    def __init__(self, attack=0.6, release=0.12):
        self.v = {k: 0.5 for k in self.KEYS}
        self.attack = attack
        self.release = release

    def update(self, raw):
        for k in self.KEYS:
            target = raw[k]
            a = self.attack if target > self.v[k] else self.release
            s = a * target + (1 - a) * self.v[k]
            self.v[k] = max(0.0, min(1.0, s))
        return dict(self.v)


class Recorder:
    """Appends one CSV row per tick (timestamp, phase, mean band powers, z-scores and the
    three smoothed 0..1 indices) so a session can be re-plotted offline by muse_plot.py.
    Phase transitions are recoverable from the `phase` column; muse_plot draws them as
    labelled vertical lines."""
    COLUMNS = (["iso_time", "t", "phase", "contact", "used"]
               + BAND_NAMES
               + ["theta_z", "alpha_z", "beta_z", "stress", "attention", "cognitive_load"])

    def __init__(self, path):
        self.path = path
        self.t0 = time.time()
        self._f = open(path, "w", buffering=1)   # line-buffered so a live plotter can tail it
        self._f.write(",".join(self.COLUMNS) + "\n")

    def row(self, phase, bands_mean, contact, used, z=None, idx=None):
        def num(x):
            return "" if x is None or (isinstance(x, float) and math.isnan(x)) else f"{x:.5g}"
        vals = [time.strftime("%Y-%m-%dT%H:%M:%S"), f"{time.time() - self.t0:.3f}",
                phase, "1" if contact else "0", "+".join(used) if used else ""]
        vals += [num(bands_mean.get(b) if bands_mean else None) for b in BAND_NAMES]
        vals += [num(z[i] if z else None) for i in (THETA, ALPHA, BETA)]
        vals += [num(idx[k] if idx else None) for k in ("stress", "attention", "cognitive_load")]
        self._f.write(",".join(vals) + "\n")

    def close(self):
        try:
            self._f.close()
        except Exception:
            pass


def mean_band_powers(bp):
    """{name: band vector} -> {band_name: mean power across good channels}, or {} if none."""
    if not bp:
        return {}
    arr = np.mean(list(bp.values()), axis=0)
    return {name: float(arr[i]) for i, name in enumerate(BAND_NAMES)}


# ── BLE reader ────────────────────────────────────────────────────────────────
class MuseReader:
    def __init__(self, name="Muse", mac="", window_secs=4.0):
        self.name = name
        self.mac = mac
        self.window_secs = window_secs
        self.peripheral = None
        maxlen = int(SAMPLE_RATE * (window_secs + 4))
        self.buffers = [collections.deque(maxlen=maxlen) for _ in EEG_CHARS]
        self.lock = threading.Lock()

    @staticmethod
    def _decode(payload):
        out = []
        for i in range(2, 20, 3):
            v1 = (payload[i] << 4) | (payload[i + 1] >> 4)
            v2 = ((payload[i + 1] & 0xF) << 8) | payload[i + 2]
            out.append((v1 - 0x800) * 125.0 / 256.0)
            out.append((v2 - 0x800) * 125.0 / 256.0)
        return out

    def _make_cb(self, idx):
        buf = self.buffers[idx]
        def cb(data):
            samples = self._decode(data)
            with self.lock:
                buf.extend(samples)
        return cb

    def _match(self, results):
        if self.mac:
            for p in results:
                if p.address().lower() == self.mac.lower():
                    return p
        # name match (works on every OS, including macOS where address is a UUID)
        for p in results:
            if self.name.lower() in (p.identifier() or "").lower():
                return p
        return None

    def connect(self, scan_ms=8000, retries=4):
        adapters = simplepyble.Adapter.get_adapters()
        if not adapters:
            raise RuntimeError("No Bluetooth adapter found — is Bluetooth turned on?")
        adapter = adapters[0]

        what = f"MAC {self.mac}" if self.mac else f'name "{self.name}"'
        target = None
        for attempt in range(1, retries + 1):
            print(f"[INFO] scanning for {what} (attempt {attempt}/{retries})...")
            adapter.scan_for(scan_ms)
            target = self._match(adapter.scan_get_results())
            if target is not None:
                break
            print("[WARN] not found — is the headset on with the LED pulsing?")
        if target is None:
            raise RuntimeError("Muse not found. Make sure it is on and advertising.")

        print(f"[OK]   found {target.identifier()} [{target.address()}]; connecting...")
        target.connect()
        list(target.services())                       # force GATT resolution
        for idx, (uuid, _) in enumerate(EEG_CHARS):
            target.notify(SERVICE, uuid, self._make_cb(idx))
        for c in START_CMDS:
            target.write_command(SERVICE, CTRL,
                                 bytes([len(c) + 1]) + c.encode() + b"\n")
            time.sleep(0.2)
        self.peripheral = target

    def get_window(self, secs):
        need = int(SAMPLE_RATE * secs)
        with self.lock:
            lengths = [len(b) for b in self.buffers]
            take = min(lengths + [need])
            if take < need // 2:
                return None, min(lengths)
            window = np.array([list(b)[-take:] for b in self.buffers], dtype=float)
        return window, take

    def disconnect(self):
        p = self.peripheral
        if p is None:
            return
        try:
            p.disconnect()
        except Exception:
            pass
        # Wait for the BLE link to ACTUALLY release. simplepyble processes the
        # disconnect on its backend thread; if we exit too early (e.g. os._exit)
        # the Muse stays in a "connected" state and won't advertise for the next
        # run — requiring a physical power-cycle. Poll is_connected() to be sure.
        deadline = time.time() + 3.0
        while time.time() < deadline:
            try:
                if not p.is_connected():
                    return
            except Exception:
                break          # is_connected() unavailable — fall back to a wait
            time.sleep(0.1)
        time.sleep(0.5)


# ── Session ─────────────────────────────────────────────────────────────────--
def run_session(args, send, recorder=None):
    reader = MuseReader(name=args.name, mac=args.mac, window_secs=args.window)
    reader.connect()
    print("[OK]   Connected and streaming.\n")
    try:
        # ── Phase 1: baseline (per-channel) ──────────────────────────────────
        print(f"{'='*60}\nPHASE 1 — BASELINE ({args.baseline:.0f}s)\n"
              "Sit still, relax, eyes open.\n" + "=" * 60)
        accum = collections.defaultdict(list)   # name -> list of band vectors
        t0 = time.time()
        while time.time() - t0 < args.baseline:
            time.sleep(args.update)
            window, have = reader.get_window(args.window)
            if window is None:
                print(f"  buffering... ({have} samples)")
                continue
            bp = per_channel_band_powers(window, SAMPLE_RATE)
            if not bp:
                print("  [WARN] poor signal — adjust headset")
                continue
            for name, vec in bp.items():
                accum[name].append(vec)
            pct = min(100.0, (time.time() - t0) / args.baseline * 100)
            mt = np.mean([v[THETA] for v in bp.values()])
            ma = np.mean([v[ALPHA] for v in bp.values()])
            print(f"  baseline [{'#'*int(pct/5):<20}] {pct:3.0f}%  "
                  f"θ={mt:.2f} α={ma:.2f}  ch={'+'.join(bp)}", end="\r")
            send({"phase": "baseline", "progress": pct / 100.0,
                  "stress": 0.5, "contact": True})
            if recorder is not None:
                recorder.row("baseline", mean_band_powers(bp), True, list(bp))
        print()

        # keep channels seen in at least half the baseline windows
        n_windows = max(len(v) for v in accum.values()) if accum else 0
        base = {name: (np.mean(v, axis=0), np.std(v, axis=0))
                for name, v in accum.items() if len(v) >= max(3, n_windows // 2)}
        if not base:
            print("[ERROR] No channel produced a stable baseline. Check contact.")
            return
        line = "  ".join(f"{n}: θ={m[THETA]:.2f}±{s[THETA]:.2f}"
                         for n, (m, s) in base.items())
        print(f"[OK] Baseline channels: {', '.join(base)}\n     {line}\n")

        # ── Phase 2: active monitoring ───────────────────────────────────────
        print(f"{'='*60}\nPHASE 2 — ACTIVE MONITORING"
              f"{'' if args.duration == 0 else f' ({args.duration:.0f}s)'}\n"
              "Start your task. Ctrl+C to stop.\n" + "=" * 60)
        print(f"{'Elapsed':>8} {'Stress':>8} {'Atten':>8} {'CogLoad':>8}  ch")
        print("-" * 56)
        smoother = IndexSmoother(attack=args.attack, release=args.release)
        t0 = time.time()
        while args.duration == 0 or time.time() - t0 < args.duration:
            time.sleep(args.update)
            elapsed = time.time() - t0
            window, _ = reader.get_window(args.window)
            bp = per_channel_band_powers(window, SAMPLE_RATE) if window is not None else {}

            res = band_zscores(bp, base)   # avg z-scores over channels in BOTH now & baseline
            if res is None:
                print(f"{elapsed:7.1f}s  [poor signal — adjust headset]")
                send({"phase": "active", "stress": smoother.v["stress"], "contact": False})
                if recorder is not None:
                    recorder.row("active", mean_band_powers(bp), False, None)
                continue

            z, used = res
            raw = indices_from_z(z, args.sensitivity)
            idx = smoother.update(raw)
            print(f"{elapsed:7.1f}s {idx['stress']:8.3f} {idx['attention']:8.3f} "
                  f"{idx['cognitive_load']:8.3f}  {'+'.join(used)}")
            send({"phase": "active", "contact": True, "theta_z": raw["theta_z"],
                  "alpha_z": raw["alpha_z"], "beta_z": raw["beta_z"], "cli": raw["cli"], **idx})
            if recorder is not None:
                recorder.row("active", mean_band_powers(bp), True, used, z, idx)
        print("\n[OK] Session complete.")
    finally:
        reader.disconnect()
        print("[OK] Disconnected.")


def run_session_unity(args, send, ctrl_sock, recorder=None):
    """Command-driven mode for the VR game: Unity's TutorialManager drives the dual
    baseline. States: idle -> rest -> idle -> active -> streaming. The bridge keeps
    computing band powers every tick; the rest/active states accumulate them, and on
    'baseline_active_stop' the ACTIVE-VR samples become the reference baseline (so
    in-game stress is measured above 'being in VR and moving', not above sitting still)."""
    reader = MuseReader(name=args.name, mac=args.mac, window_secs=args.window)
    reader.connect()
    print(f"[OK]   Connected. Waiting for Unity to drive the baseline "
          f"(control udp:{args.control_port}).\n")

    commands = collections.deque()
    running = {"on": True}

    def ctrl_loop():
        while running["on"]:
            try:
                data, addr = ctrl_sock.recvfrom(1024)
            except OSError:
                break
            for line in data.decode(errors="ignore").split("\n"):
                line = line.strip()
                if not line:
                    continue
                try:
                    commands.append((json.loads(line).get("cmd", ""), addr))
                except Exception:
                    commands.append((line, addr))

    def ack(addr, cmd):
        # Acknowledge a command back to its sender so Unity's baseline countdown only starts
        # once the bridge has actually entered the phase (the "delay acknowledgement").
        try:
            ctrl_sock.sendto((json.dumps({"ack": cmd}) + "\n").encode(), addr)
        except OSError:
            pass

    threading.Thread(target=ctrl_loop, daemon=True, name="MuseCtrl").start()

    state = "idle"
    rest = collections.defaultdict(list)
    active = collections.defaultdict(list)
    base = {}
    smoother = IndexSmoother()
    try:
        while True:
            time.sleep(args.update)
            while commands:
                cmd, addr = commands.popleft()
                if cmd == "baseline_rest_start":
                    state, rest = "rest", collections.defaultdict(list)
                    print("[CTRL] rest baseline started")
                elif cmd == "baseline_rest_stop":
                    state = "idle"
                    print(f"[CTRL] rest baseline stopped "
                          f"({max((len(v) for v in rest.values()), default=0)} windows)")
                elif cmd == "baseline_active_start":
                    state, active = "active", collections.defaultdict(list)
                    print("[CTRL] active-VR baseline started")
                elif cmd == "baseline_active_stop":
                    base = finalize_baseline(active)
                    if base:
                        state = "streaming"
                        print(f"[CTRL] baseline finalized on {'+'.join(base)} -> streaming")
                    else:
                        state = "idle"
                        print("[CTRL] baseline finalize FAILED (no stable channel) -> idle")
                elif cmd == "reset":
                    state, base = "idle", {}
                    rest, active = collections.defaultdict(list), collections.defaultdict(list)
                    print("[CTRL] reset")
                ack(addr, cmd)

            window, _ = reader.get_window(args.window)
            bp = per_channel_band_powers(window, SAMPLE_RATE) if window is not None else {}
            bands_mean = mean_band_powers(bp)

            def record(phase, contact, used=None, z=None, idx=None):
                if recorder is not None:
                    recorder.row(phase, bands_mean, contact, used, z, idx)

            if state == "rest":
                for n, v in bp.items():
                    rest[n].append(v)
                # Stream the raw band powers during the baseline too — there are no standardized
                # indices yet (we're still measuring the reference), so the Unity HUD shows live
                # band activity + contact instead, to confirm the headset is reading well.
                send({"phase": "baseline_rest", "stress": 0.5, "contact": bool(bp), **bands_mean})
                record("baseline_rest", bool(bp), list(bp))
            elif state == "active":
                for n, v in bp.items():
                    active[n].append(v)
                send({"phase": "baseline_active", "stress": 0.5, "contact": bool(bp), **bands_mean})
                record("baseline_active", bool(bp), list(bp))
            elif state == "streaming":
                res = band_zscores(bp, base)
                if res is None:
                    send({"phase": "active", "stress": smoother.v["stress"], "contact": False})
                    record("active", False)
                    continue
                z, used = res
                raw = indices_from_z(z, args.sensitivity)
                idx = smoother.update(raw)
                send({"phase": "active", "contact": True,
                      "theta_z": raw["theta_z"], "alpha_z": raw["alpha_z"],
                      "beta_z": raw["beta_z"], "cli": raw["cli"], **idx})
                record("active", True, used, z, idx)
                print(f"  stress={idx['stress']:.3f} att={idx['attention']:.3f} "
                      f"cog={idx['cognitive_load']:.3f}  {'+'.join(used)}")
            else:  # idle
                send({"phase": "idle", "stress": 0.5, "contact": bool(bp), **bands_mean})
                record("idle", bool(bp), list(bp))
    except KeyboardInterrupt:
        print("\n[INFO] Stopped by user.")
    finally:
        running["on"] = False
        try:
            ctrl_sock.close()
        except Exception:
            pass
        reader.disconnect()
        print("[OK] Disconnected.")


def _launch_live_plot(csv_path):
    """Open Tools/muse_plot.py in --live mode as a separate process so a plot window tracks the
    session (raw bands + the three indices) in real time. Returns the Popen handle, or None if it
    couldn't start (e.g. matplotlib not installed) — the bridge keeps running regardless."""
    try:
        import importlib.util
        if importlib.util.find_spec("matplotlib") is None:
            print("[INFO] matplotlib not installed — skipping live plot "
                  "(pip install matplotlib numpy, or pass --no-plot to silence).")
            return None
        plot_py = os.path.join(os.path.dirname(os.path.abspath(__file__)), "muse_plot.py")
        proc = subprocess.Popen([sys.executable, plot_py, csv_path, "--live", "--bands"])
        print(f"[INFO] live plot window -> {csv_path}")
        return proc
    except Exception as e:
        print(f"[INFO] could not open live plot ({e}); continuing without it.")
        return None


def main():
    ap = argparse.ArgumentParser(description="Muse S Athena -> Unity UDP bridge")
    ap.add_argument("--name", default="Muse",
                    help='Match device by name (default "Muse"). Works on all OSes.')
    ap.add_argument("--mac", default="",
                    help="Match by MAC (Linux/Windows only; macOS hides the MAC).")
    ap.add_argument("--baseline", type=float, default=60.0)
    ap.add_argument("--window", type=float, default=4.0,
                    help="EEG seconds per band-power window (default 4).")
    ap.add_argument("--update", type=float, default=1.0,
                    help="Seconds between updates / lower = lower latency (default 1).")
    ap.add_argument("--sensitivity", type=float, default=1.5)
    ap.add_argument("--attack", type=float, default=0.6,
                    help="Peak-follower rise rate (0..1). Higher = the index jumps to a peak faster "
                         "(more reactive to bursts of stress). Default 0.6.")
    ap.add_argument("--release", type=float, default=0.12,
                    help="Peak-follower fall rate (0..1). Lower = peaks linger / decay slower. "
                         "Default 0.12. Set --release equal to --attack for plain EMA smoothing.")
    ap.add_argument("--duration", type=float, default=0.0,
                    help="Active monitoring seconds (0 = run until Ctrl+C)")
    ap.add_argument("--udp-host", default="127.0.0.1")
    ap.add_argument("--udp-port", type=int, default=5005)
    ap.add_argument("--no-udp", action="store_true")
    ap.add_argument("--verbose", action="store_true",
                    help="Show the SimpleBLE native library's stderr chatter.")
    ap.add_argument("--unity", action="store_true",
                    help="VR-game mode: let Unity's TutorialManager drive the dual "
                         "(rest + active-VR) baseline via control UDP. Without this the "
                         "bridge runs its own single rest baseline.")
    ap.add_argument("--control-port", type=int, default=5006,
                    help="UDP port to receive Unity baseline commands on (--unity mode).")
    ap.add_argument("--record", nargs="?", const="auto", default=None, metavar="PATH",
                    help="Log every tick (phase, band powers, the 3 indices) to a CSV for "
                         "offline/live plotting with Tools/muse_plot.py. Bare --record auto-names "
                         "Tools/sessions/session_<timestamp>.csv.")
    ap.add_argument("--no-plot", action="store_true",
                    help="Don't auto-open the live plot window. By default the bridge records the "
                         "session and opens Tools/muse_plot.py --live so you see the raw bands and "
                         "the three indices in real time.")
    # Restore real stderr so argparse errors/usage are visible, parse, then re-mute.
    if _SAVED_STDERR_FD is not None:
        os.dup2(_SAVED_STDERR_FD, 2)
    args = ap.parse_args()

    try:                                    # live output even when piped to grep/tee
        sys.stdout.reconfigure(line_buffering=True)
    except Exception:
        pass

    if not args.verbose and _SAVED_STDERR_FD is not None:
        mute_native_stderr()
    _load_simplepyble()        # import AFTER muting so the backend banner is hidden

    sock = None
    if not args.no_udp:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        print(f"[INFO] UDP -> {args.udp_host}:{args.udp_port}")

    def send(payload):
        if sock is not None:
            sock.sendto((json.dumps(payload) + "\n").encode(),
                        (args.udp_host, args.udp_port))

    # Record the session if --record was given OR if we're going to live-plot (the plot reads the
    # CSV the recorder writes), so the default `--unity` run both logs and shows a live window.
    recorder = None
    record_path = None
    if args.record is not None or not args.no_plot:
        record_path = args.record if (args.record and args.record != "auto") else None
        if record_path is None:
            sess_dir = os.path.join(os.path.dirname(os.path.abspath(__file__)), "sessions")
            os.makedirs(sess_dir, exist_ok=True)
            record_path = os.path.join(sess_dir, f"session_{time.strftime('%Y%m%d_%H%M%S')}.csv")
        recorder = Recorder(record_path)
        print(f"[INFO] recording -> {record_path}")

    plot_proc = None
    if not args.no_plot and record_path is not None:
        plot_proc = _launch_live_plot(record_path)

    ctrl_sock = None
    try:
        if args.unity:
            ctrl_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            ctrl_sock.bind(("0.0.0.0", args.control_port))
            run_session_unity(args, send, ctrl_sock, recorder)
        else:
            run_session(args, send, recorder)
    except KeyboardInterrupt:
        print("\n[INFO] Stopped by user.")
    except Exception as e:
        print(f"\n[ERROR] {e}")
        if args.verbose:
            import traceback
            traceback.print_exc(file=sys.stdout)
    finally:
        if sock is not None:
            sock.close()
        if ctrl_sock is not None:
            try:
                ctrl_sock.close()
            except Exception:
                pass
        if recorder is not None:
            recorder.close()
        if plot_proc is not None:
            try:
                plot_proc.terminate()
            except Exception:
                pass

    # Exit immediately. The SimpleBLE backend's daemon threads emit harmless D-Bus
    # chatter from C++ static destructors at interpreter shutdown (it caches the
    # original stderr, evading our redirect). os._exit skips that teardown.
    sys.stdout.flush()
    os._exit(0)


if __name__ == "__main__":
    main()
