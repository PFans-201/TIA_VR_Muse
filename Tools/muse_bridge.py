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
import sys
import threading
import time

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
THETA, ALPHA = 1, 2  # indices into the band vector
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
def run_session(args, send):
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
        print(f"{'Elapsed':>8} {'θ z':>8} {'α z':>8} {'CLI':>8} {'Stress':>8}  ch")
        print("-" * 56)
        smoothed = 0.5
        t0 = time.time()
        while args.duration == 0 or time.time() - t0 < args.duration:
            time.sleep(args.update)
            elapsed = time.time() - t0
            window, _ = reader.get_window(args.window)
            bp = per_channel_band_powers(window, SAMPLE_RATE) if window is not None else {}

            # average per-channel z-scores over channels present in BOTH now & baseline
            tz, az, used = [], [], []
            for name, vec in bp.items():
                if name not in base:
                    continue
                m, s = base[name]
                tz.append((vec[THETA] - m[THETA]) / s[THETA] if s[THETA] > 1e-9 else 0.0)
                az.append((vec[ALPHA] - m[ALPHA]) / s[ALPHA] if s[ALPHA] > 1e-9 else 0.0)
                used.append(name)
            if not used:
                print(f"{elapsed:7.1f}s  [poor signal — adjust headset]")
                send({"phase": "active", "stress": smoothed, "contact": False})
                continue

            theta_z = float(np.mean(tz))
            alpha_z = float(np.mean(az))
            cli = (theta_z - alpha_z) / 2.0
            raw = sigmoid(cli / args.sensitivity)
            smoothed = max(0.0, min(1.0, SMOOTH_ALPHA * raw + (1 - SMOOTH_ALPHA) * smoothed))
            print(f"{elapsed:7.1f}s {theta_z:+8.2f} {alpha_z:+8.2f} "
                  f"{cli:+8.2f} {smoothed:8.3f}  {'+'.join(used)}")
            send({"phase": "active", "stress": smoothed, "theta_z": theta_z,
                  "alpha_z": alpha_z, "cli": cli, "contact": True})
        print("\n[OK] Session complete.")
    finally:
        reader.disconnect()
        print("[OK] Disconnected.")


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
    ap.add_argument("--duration", type=float, default=0.0,
                    help="Active monitoring seconds (0 = run until Ctrl+C)")
    ap.add_argument("--udp-host", default="127.0.0.1")
    ap.add_argument("--udp-port", type=int, default=5005)
    ap.add_argument("--no-udp", action="store_true")
    ap.add_argument("--verbose", action="store_true",
                    help="Show the SimpleBLE native library's stderr chatter.")
    args = ap.parse_args()

    try:                                    # live output even when piped to grep/tee
        sys.stdout.reconfigure(line_buffering=True)
    except Exception:
        pass

    if not args.verbose:
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

    try:
        run_session(args, send)
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

    # Exit immediately. The SimpleBLE backend's daemon threads emit harmless D-Bus
    # chatter from C++ static destructors at interpreter shutdown (it caches the
    # original stderr, evading our redirect). os._exit skips that teardown.
    sys.stdout.flush()
    os._exit(0)


if __name__ == "__main__":
    main()
