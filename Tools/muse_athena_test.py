"""
Muse S Athena — BrainFlow connectivity and cognitive-load test
==============================================================

PURPOSE
    Standalone script to confirm the Muse S Athena connects via Bluetooth
    and produces a valid baseline-corrected cognitive-load signal.

    WHY DIFFERENT FROM THE COLLEAGUES' test_muse.py
    ─────────────────────────────────────────────────
    The colleagues used a β/α ratio with min/max normalisation over a
    "concentration task" window.  Two problems:
      1. β/α measures focused *attention*, not cognitive *overload*.
         Under overload, frontal theta (4–8 Hz) increases and parietal
         alpha (8–13 Hz) decreases — the theta/alpha ratio is the
         established cognitive-load index (Klimesch 1999).
      2. Min/max normalisation over an active task window mixes the
         resting and loaded states into the same calibration window.
         When the actual task is harder than anything in calibration,
         the metric saturates at 1.0 and stops being useful.

    THIS SCRIPT uses:
      • Band-pass filter 1–50 Hz before FFT (removes DC and powerline noise)
      • Frontal theta ↑ + parietal alpha ↓ as the cognitive-load index (CLI)
      • A SEPARATE resting baseline phase (60 s, no task)
      • Z-score normalisation: how many σ above/below the resting level?
      • Sigmoid mapping to [0, 1] — won't saturate when load exceeds baseline range
      • Exponential smoothing (α=0.25) — stable but not sluggish

INSTALL
    pip install brainflow

    (or from the local clone in this repo)
    pip install -e ../brainflow/python_package/

FIND YOUR DEVICE MAC ADDRESS (Linux)
    $ bluetoothctl
    [bluetoothctl] scan on
    # Wait for "Muse-XXXX" — note the XX:XX:XX:XX:XX:XX address
    [bluetoothctl] scan off
    [bluetoothctl] exit

USAGE
    python Tools/muse_athena_test.py --mac D4:22:CD:00:AA:BB
    python Tools/muse_athena_test.py --mac D4:22:CD:00:AA:BB --baseline 90
    python Tools/muse_athena_test.py --mac D4:22:CD:00:AA:BB --sensitivity 2.0
"""

import argparse
import time
import sys
import math

try:
    import numpy as np
    from brainflow.board_shim import (
        BoardShim, BoardIds, BrainFlowInputParams, BrainFlowPresets
    )
    from brainflow.data_filter import DataFilter, FilterTypes
    from brainflow.ml_model import (
        MLModel, BrainFlowModelParams, BrainFlowMetrics, BrainFlowClassifiers
    )
except ImportError as e:
    print(f"[ERROR] Missing dependency: {e}")
    print("Run:  pip install brainflow numpy")
    sys.exit(1)

# ── Constants ─────────────────────────────────────────────────────────────────

BOARD_ID       = BoardIds.MUSE_S_ATHENA_BOARD.value   # 67
UPDATE_INTERVAL = 2.0    # seconds between metric prints
WINDOW_SECS    = 4.0     # EEG history per band-power window

# Muse S Athena ancillary preset (optical/infrared PPG) at 64 Hz
ANC_PRESET     = BrainFlowPresets.ANCILLARY_PRESET.value

# ── Signal processing ─────────────────────────────────────────────────────────

def get_filtered_band_powers(data, eeg_channels, sampling_rate):
    """
    Band-pass filter 1-50 Hz, then compute [delta, theta, alpha, beta, gamma]
    average band powers across all EEG channels.
    Returns None if signal quality is too poor (flat or saturated).
    """
    cols = data.shape[1]
    rms = np.sqrt(np.mean(data[eeg_channels, :] ** 2))
    if rms < 0.5 or rms > 800.0:
        return None   # poor contact

    filtered = data.copy()
    for ch in eeg_channels:
        DataFilter.perform_bandpass(
            filtered[ch, :], sampling_rate, 1.0, 50.0, 4,
            FilterTypes.BUTTERWORTH.value, 0.0
        )

    bands = DataFilter.get_avg_band_powers(filtered, eeg_channels, sampling_rate, False)
    bp = bands[0]   # [delta, theta, alpha, beta, gamma]

    if np.any(np.isnan(bp)) or np.any(np.isinf(bp)):
        return None

    return bp


def z_score(value, mean, std):
    return (value - mean) / std if std > 1e-9 else 0.0


def sigmoid(x):
    return 1.0 / (1.0 + math.exp(-x))


# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Muse S Athena cognitive-load test")
    parser.add_argument("--mac",         default="",   help="Bluetooth MAC, e.g. D4:22:CD:00:AA:BB")
    parser.add_argument("--baseline",    default=60.0, type=float,
                        help="Baseline recording duration in seconds (default 60)")
    parser.add_argument("--sensitivity", default=1.5,  type=float,
                        help="Stress sensitivity — CLI z-score that maps to 0.73 (default 1.5)")
    parser.add_argument("--duration",    default=120.0, type=float,
                        help="Active monitoring duration after baseline (default 120 s)")
    args = parser.parse_args()

    params = BrainFlowInputParams()
    if args.mac:
        params.mac_address = args.mac
    else:
        print("[WARN] No MAC address — attempting auto-discovery (may be slow on Linux).")

    board = BoardShim(BOARD_ID, params)
    BoardShim.enable_dev_board_logger()

    try:
        print(f"\n[INFO] Connecting to Muse S Athena (board_id={BOARD_ID})...")
        board.prepare_session()
        board.start_stream(45000)
        print("[OK]   Connected.\n")

        sampling_rate  = BoardShim.get_sampling_rate(BOARD_ID)
        eeg_channels   = BoardShim.get_eeg_channels(BOARD_ID)
        eeg_names      = BoardShim.get_eeg_names(BOARD_ID)   # TP9, AF7, AF8, TP10
        samples_needed = int(sampling_rate * WINDOW_SECS)

        try:
            opt_channels = BoardShim.get_optical_channels(BOARD_ID, ANC_PRESET)
            anc_rate     = BoardShim.get_sampling_rate(BOARD_ID, ANC_PRESET)
            has_optical  = len(opt_channels) > 0
        except Exception:
            has_optical  = False
            anc_rate     = 64

        print(f"EEG:     {sampling_rate} Hz — {list(zip(eeg_channels, eeg_names.split(',')))}")
        print(f"Optical: {anc_rate} Hz — available={has_optical}")

        # ── Phase 1: Baseline calibration ─────────────────────────────────
        print(f"\n{'='*60}")
        print(f"PHASE 1 — BASELINE ({args.baseline:.0f}s)")
        print("Sit still, relax, eyes open. Do NOT start any task yet.")
        print(f"{'='*60}\n")

        baseline_samples = []
        start = time.time()

        while True:
            time.sleep(UPDATE_INTERVAL)
            elapsed = time.time() - start

            raw = board.get_current_board_data(samples_needed)
            if raw.shape[1] < samples_needed // 2:
                print(f"  Buffering... ({raw.shape[1]}/{samples_needed} samples)")
                continue

            bp = get_filtered_band_powers(raw, eeg_channels, sampling_rate)
            if bp is None:
                print("  [WARN] Poor signal — adjust headset")
                continue

            baseline_samples.append(bp)
            pct = min(100.0, (elapsed / args.baseline) * 100)
            bar = "▓" * int(pct / 5)
            print(f"  Baseline [{bar:<20}] {pct:.0f}%  "
                  f"θ={bp[1]:.3f}  α={bp[2]:.3f}", end="\r")

            if elapsed >= args.baseline:
                break

        print()  # newline after \r

        if not baseline_samples:
            print("[ERROR] No clean baseline windows recorded. Check headset contact.")
            return

        baseline_arr = np.array(baseline_samples)
        base_mean    = baseline_arr.mean(axis=0)  # [δ,θ,α,β,γ]
        base_std     = baseline_arr.std(axis=0)

        print(f"\n[OK] Baseline captured ({len(baseline_samples)} windows)")
        print(f"     θ mean={base_mean[1]:.3f} ± {base_std[1]:.3f}")
        print(f"     α mean={base_mean[2]:.3f} ± {base_std[2]:.3f}")

        # ── Phase 2: Active monitoring ─────────────────────────────────────
        print(f"\n{'='*60}")
        print(f"PHASE 2 — ACTIVE MONITORING ({args.duration:.0f}s)")
        print("Start your task now. Ctrl+C to stop early.")
        print(f"{'='*60}\n")
        print(f"{'Elapsed':>8}  {'θ z-score':>10}  {'α z-score':>10}  "
              f"{'CLI':>8}  {'Stress':>8}  {'Optical':>10}")
        print("-" * 70)

        smoothed_stress = 0.5
        active_start    = time.time()

        while time.time() - active_start < args.duration:
            time.sleep(UPDATE_INTERVAL)
            elapsed = time.time() - active_start

            raw = board.get_current_board_data(samples_needed)
            bp  = get_filtered_band_powers(raw, eeg_channels, sampling_rate)
            if bp is None:
                print(f"{elapsed:7.1f}s   [poor signal — adjust headset]")
                continue

            theta_z = z_score(bp[1], base_mean[1], base_std[1])
            alpha_z = z_score(bp[2], base_mean[2], base_std[2])
            cli     = (theta_z - alpha_z) / 2.0    # positive when loaded

            raw_stress     = sigmoid(cli / args.sensitivity)
            smoothed_stress = 0.25 * raw_stress + 0.75 * smoothed_stress
            smoothed_stress = max(0.0, min(1.0, smoothed_stress))

            # Optical (infrared PPG)
            optical_str = "N/A"
            if has_optical:
                try:
                    anc = board.get_current_board_data(
                        anc_rate * 4, ANC_PRESET)
                    optical = float(np.mean(np.abs(anc[opt_channels[0], :])))
                    optical_str = f"{optical:10.1f}"
                except Exception:
                    pass

            bar = "█" * int(smoothed_stress * 20)
            print(f"{elapsed:7.1f}s   {theta_z:+10.3f}  {alpha_z:+10.3f}  "
                  f"{cli:+8.3f}  {smoothed_stress:8.3f}  {optical_str}")

        print(f"\n[OK] Session complete.")

    except KeyboardInterrupt:
        print("\n[INFO] Stopped by user.")

    except Exception as e:
        print(f"\n[ERROR] {e}")
        print("\nTroubleshooting:")
        print("  rfkill unblock bluetooth")
        print("  sudo systemctl start bluetooth")
        print("  sudo pacman -S bluez bluez-utils   # Arch Linux")
        print("  bluetoothctl → pair XX:XX:XX:XX:XX:XX")

    finally:
        try:
            board.stop_stream()
            board.release_session()
            print("[OK] Session released.")
        except Exception:
            pass


if __name__ == "__main__":
    main()
