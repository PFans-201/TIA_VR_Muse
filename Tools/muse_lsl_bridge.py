"""
Muse LSL → Unity UDP bridge (via muselsl)
=========================================

WHY THIS EXISTS
    The Quest 2's Bluetooth stack cannot pair with the Muse headband.
    This script runs on a Mac (or any PC) that IS paired with the Muse via
    muselsl, receives the raw EEG over LSL, performs the same DSP / baseline /
    stress pipeline as muse_bridge.py, and sends the result over WiFi-UDP to
    the Quest 2 running the VR game.

NETWORK SETUP
    1. Create a WiFi hotspot on your phone (iPhone, etc.).
    2. Connect BOTH this Mac AND the Quest 2 to that hotspot.
    3. Note the Quest 2's IP address (Settings → Wi-Fi → select network → IP).

WORKFLOW
    Terminal 1 — start the Muse LSL stream:
        muselsl stream              # or: muselsl stream --name Muse-XXXX

    Terminal 2 — start this bridge (point it at the Quest's IP):
        python Tools/muse_lsl_bridge.py --quest-ip <QUEST_IP>

    Terminal 2 (Unity-driven baseline mode, for the VR game):
        python Tools/muse_lsl_bridge.py --quest-ip <QUEST_IP> --unity

    The Unity app on the Quest receives JSON-per-line UDP on port 5005 —
    exactly what MuseUdpAdapter.cs already expects.

DEPENDENCIES
    pip install muselsl pylsl numpy
"""

import argparse
import collections
import json
import math
import socket
import sys
import threading
import time

import numpy as np
from pylsl import StreamInlet, resolve_byprop

# ── Muse constants ────────────────────────────────────────────────────────────
EEG_NAMES   = ["TP9", "AF7", "AF8", "TP10"]
SAMPLE_RATE = 256                       # Hz (Muse 2 / Muse S)
NUM_CHANNELS = 4

# ── Signal-processing constants (match muse_bridge.py exactly) ────────────────
RMS_MIN, RMS_MAX = 0.5, 800.0
RAIL_UV = 990.0
SMOOTH_ALPHA = 0.25
THETA, ALPHA = 1, 2
BANDS = [(1.0, 4.0), (4.0, 8.0), (8.0, 13.0), (13.0, 30.0), (30.0, 45.0)]


# ── DSP (identical to muse_bridge.py) ─────────────────────────────────────────
def _channel_band_powers(x, fs):
    n = len(x)
    freqs = np.fft.rfftfreq(n, d=1.0 / fs)
    hann = np.hanning(n)
    win_power = np.sum(hann ** 2)
    x = x - np.mean(x)
    spec = np.fft.rfft(x * hann)
    psd = (np.abs(spec) ** 2) / (fs * win_power)
    psd[1:-1] *= 2.0
    return np.array([np.sum(psd[(freqs >= lo) & (freqs < hi)]) * (fs / n)
                     for (lo, hi) in BANDS])


def per_channel_band_powers(window, fs):
    out = {}
    for ch_idx, (ch, name) in enumerate(zip(window, EEG_NAMES)):
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
    n = max((len(v) for v in samples.values()), default=0)
    return {name: (np.mean(v, axis=0), np.std(v, axis=0))
            for name, v in samples.items() if len(v) >= max(3, n // 2)}


def stress_from_bp(bp, base):
    tz, az, used = [], [], []
    for name, vec in bp.items():
        if name not in base:
            continue
        m, s = base[name]
        tz.append((vec[THETA] - m[THETA]) / s[THETA] if s[THETA] > 1e-9 else 0.0)
        az.append((vec[ALPHA] - m[ALPHA]) / s[ALPHA] if s[ALPHA] > 1e-9 else 0.0)
        used.append(name)
    if not used:
        return None
    theta_z = float(np.mean(tz))
    alpha_z = float(np.mean(az))
    return theta_z, alpha_z, (theta_z - alpha_z) / 2.0, used


# ── LSL reader ────────────────────────────────────────────────────────────────
class MuseLslReader:
    """Reads from an LSL EEG stream (e.g. produced by `muselsl stream`) and
    buffers into per-channel deques, matching the MuseReader interface."""

    def __init__(self, window_secs=4.0, stream_name=None):
        self.window_secs = window_secs
        self.stream_name = stream_name
        self.inlet = None
        maxlen = int(SAMPLE_RATE * (window_secs + 4))
        self.buffers = [collections.deque(maxlen=maxlen) for _ in range(NUM_CHANNELS)]
        self.lock = threading.Lock()
        self._running = False
        self._thread = None

    def connect(self, timeout=30.0):
        print(f"[INFO] Resolving LSL stream (type='EEG'"
              f"{f', name={self.stream_name!r}' if self.stream_name else ''})...")
        if self.stream_name:
            streams = resolve_byprop('name', self.stream_name, timeout=timeout)
        else:
            streams = resolve_byprop('type', 'EEG', timeout=timeout)
        if not streams:
            raise RuntimeError("No EEG LSL stream found. Is `muselsl stream` running?")
        info = streams[0]
        print(f"[OK]   Found stream: {info.name()}, {info.channel_count()} channels, "
              f"{info.nominal_srate()} Hz")
        self.inlet = StreamInlet(streams[0], max_chunklen=12)
        # Start the background pull thread
        self._running = True
        self._thread = threading.Thread(target=self._pull_loop, daemon=True, name="LSLPull")
        self._thread.start()

    def _pull_loop(self):
        while self._running:
            try:
                # pull_chunk returns (samples_list, timestamps_list)
                # each sample is [ch0, ch1, ch2, ch3]
                samples, timestamps = self.inlet.pull_chunk(timeout=0.5, max_samples=64)
                if samples:
                    with self.lock:
                        for sample in samples:
                            for ch_idx in range(min(NUM_CHANNELS, len(sample))):
                                self.buffers[ch_idx].append(sample[ch_idx])
            except Exception as e:
                print(f"[WARN] LSL pull error: {e}")
                time.sleep(0.5)

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
        self._running = False
        if self._thread:
            self._thread.join(2.0)
        self.inlet = None


# ── Session (standalone baseline — no Unity control) ──────────────────────────
def run_session(args, send, reader):
    print("[OK]   LSL stream connected and buffering.\n")
    try:
        # Phase 1: baseline
        print(f"{'='*60}\nPHASE 1 — BASELINE ({args.baseline:.0f}s)\n"
              "Sit still, relax, eyes open.\n" + "=" * 60)
        accum = collections.defaultdict(list)
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

        base = finalize_baseline(accum)
        if not base:
            print("[ERROR] No channel produced a stable baseline. Check contact.")
            return
        line = "  ".join(f"{n}: θ={m[THETA]:.2f}±{s[THETA]:.2f}"
                         for n, (m, s) in base.items())
        print(f"[OK] Baseline channels: {', '.join(base)}\n     {line}\n")

        # Phase 2: active monitoring
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

            res = stress_from_bp(bp, base)
            if res is None:
                print(f"{elapsed:7.1f}s  [poor signal — adjust headset]")
                send({"phase": "active", "stress": smoothed, "contact": False})
                continue

            theta_z, alpha_z, cli, used = res
            raw = sigmoid(cli / args.sensitivity)
            smoothed = max(0.0, min(1.0, SMOOTH_ALPHA * raw + (1 - SMOOTH_ALPHA) * smoothed))
            print(f"{elapsed:7.1f}s {theta_z:+8.2f} {alpha_z:+8.2f} "
                  f"{cli:+8.2f} {smoothed:8.3f}  {'+'.join(used)}")
            send({"phase": "active", "stress": smoothed, "theta_z": theta_z,
                  "alpha_z": alpha_z, "cli": cli, "contact": True})
        print("\n[OK] Session complete.")
    finally:
        reader.disconnect()
        print("[OK] Disconnected from LSL.")


# ── Session (Unity-driven dual baseline) ──────────────────────────────────────
def run_session_unity(args, send, reader, ctrl_sock):
    """Command-driven mode: Unity's TutorialManager drives the dual baseline."""
    print(f"[OK]   LSL stream connected. Waiting for Unity baseline commands "
          f"(control udp:{args.control_port}).\n")

    commands = collections.deque()
    running = {"on": True}

    def ctrl_loop():
        while running["on"]:
            try:
                data, _ = ctrl_sock.recvfrom(1024)
            except OSError:
                break
            for line in data.decode(errors="ignore").split("\n"):
                line = line.strip()
                if not line:
                    continue
                try:
                    commands.append(json.loads(line).get("cmd", ""))
                except Exception:
                    commands.append(line)

    threading.Thread(target=ctrl_loop, daemon=True, name="MuseCtrl").start()

    state = "idle"
    rest = collections.defaultdict(list)
    active = collections.defaultdict(list)
    base = {}
    smoothed = 0.5
    try:
        while True:
            time.sleep(args.update)
            while commands:
                cmd = commands.popleft()
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
                    rest = collections.defaultdict(list)
                    active = collections.defaultdict(list)
                    print("[CTRL] reset")

            window, _ = reader.get_window(args.window)
            bp = per_channel_band_powers(window, SAMPLE_RATE) if window is not None else {}

            if state == "rest":
                for n, v in bp.items():
                    rest[n].append(v)
                send({"phase": "baseline_rest", "stress": 0.5, "contact": bool(bp)})
            elif state == "active":
                for n, v in bp.items():
                    active[n].append(v)
                send({"phase": "baseline_active", "stress": 0.5, "contact": bool(bp)})
            elif state == "streaming":
                res = stress_from_bp(bp, base)
                if res is None:
                    send({"phase": "active", "stress": smoothed, "contact": False})
                    continue
                theta_z, alpha_z, cli, used = res
                raw = sigmoid(cli / args.sensitivity)
                smoothed = max(0.0, min(1.0,
                               SMOOTH_ALPHA * raw + (1 - SMOOTH_ALPHA) * smoothed))
                send({"phase": "active", "stress": smoothed, "theta_z": theta_z,
                      "alpha_z": alpha_z, "cli": cli, "contact": True})
                print(f"  stress={smoothed:.3f}  θz={theta_z:+.2f}  "
                      f"αz={alpha_z:+.2f}  {'+'.join(used)}")
            else:  # idle
                send({"phase": "idle", "stress": 0.5, "contact": bool(bp)})
    except KeyboardInterrupt:
        print("\n[INFO] Stopped by user.")
    finally:
        running["on"] = False
        try:
            ctrl_sock.close()
        except Exception:
            pass
        reader.disconnect()
        print("[OK] Disconnected from LSL.")


def main():
    ap = argparse.ArgumentParser(
        description="Muse LSL → Unity UDP bridge (for Quest-over-WiFi)")
    ap.add_argument("--quest-ip", default="127.0.0.1",
                    help="IP address of the Quest 2 on the WiFi network "
                         "(default 127.0.0.1 for local editor testing).")
    ap.add_argument("--udp-port", type=int, default=5005,
                    help="Port the Quest/Unity listens on (default 5005).")
    ap.add_argument("--stream-name", default=None,
                    help="LSL stream name to resolve (default: any EEG stream).")
    ap.add_argument("--baseline", type=float, default=60.0,
                    help="Baseline duration in seconds (standalone mode).")
    ap.add_argument("--window", type=float, default=4.0,
                    help="EEG seconds per band-power window (default 4).")
    ap.add_argument("--update", type=float, default=1.0,
                    help="Seconds between updates (default 1).")
    ap.add_argument("--sensitivity", type=float, default=1.5,
                    help="Stress sensitivity (higher = harder to move off 0.5).")
    ap.add_argument("--duration", type=float, default=0.0,
                    help="Active monitoring seconds (0 = until Ctrl+C).")
    ap.add_argument("--unity", action="store_true",
                    help="Let Unity drive the dual baseline via control UDP.")
    ap.add_argument("--control-port", type=int, default=5006,
                    help="UDP port to receive Unity baseline commands (--unity mode).")

    args = ap.parse_args()

    try:
        sys.stdout.reconfigure(line_buffering=True)
    except Exception:
        pass

    # Setup LSL reader
    reader = MuseLslReader(window_secs=args.window, stream_name=args.stream_name)
    reader.connect()

    # Setup UDP sender → Quest
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    print(f"[INFO] UDP → {args.quest_ip}:{args.udp_port}")

    def send(payload):
        sock.sendto((json.dumps(payload) + "\n").encode(),
                    (args.quest_ip, args.udp_port))

    ctrl_sock = None
    try:
        if args.unity:
            ctrl_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            ctrl_sock.bind(("0.0.0.0", args.control_port))
            run_session_unity(args, send, reader, ctrl_sock)
        else:
            run_session(args, send, reader)
    except KeyboardInterrupt:
        print("\n[INFO] Stopped by user.")
    except Exception as e:
        print(f"\n[ERROR] {e}")
        import traceback
        traceback.print_exc()
    finally:
        sock.close()
        if ctrl_sock is not None:
            try:
                ctrl_sock.close()
            except Exception:
                pass


if __name__ == "__main__":
    main()
