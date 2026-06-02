"""
Muse S Athena — BrainFlow connectivity and metrics test
========================================================

PURPOSE
    Standalone script to confirm the Muse S Athena headset can connect via
    Bluetooth and produce valid EEG + infrared (optical PPG) data.
    Run this BEFORE integrating with Unity so you know the device works.

INSTALL
    pip install brainflow

    (or from the local clone in this repo)
    pip install -e ../brainflow/python_package/

FIND YOUR DEVICE MAC ADDRESS (Linux)
    $ bluetoothctl
    [bluetoothctl] scan on
    # Wait for "Muse-XXXX" to appear — note the XX:XX:XX:XX:XX:XX address
    [bluetoothctl] scan off
    [bluetoothctl] exit

USAGE
    # With known MAC (recommended — faster, more reliable)
    python muse_athena_test.py --mac D4:22:CD:00:AA:BB

    # Auto-discover (slower, may not work on all Linux BT stacks)
    python muse_athena_test.py

    # Run for a custom duration (default: 30 s)
    python muse_athena_test.py --mac D4:22:CD:00:AA:BB --duration 60
"""

import argparse
import time
import sys

# ── Imports ───────────────────────────────────────────────────────────────────

try:
    from brainflow.board_shim import (
        BoardShim, BoardIds, BrainFlowInputParams, BrainFlowPresets
    )
    from brainflow.data_filter import DataFilter
    from brainflow.ml_model import (
        MLModel, BrainFlowModelParams, BrainFlowMetrics, BrainFlowClassifiers
    )
except ImportError:
    print("[ERROR] brainflow not installed. Run:  pip install brainflow")
    sys.exit(1)

# ── Constants ─────────────────────────────────────────────────────────────────

BOARD_ID        = BoardIds.MUSE_S_ATHENA_BOARD.value   # 67
UPDATE_INTERVAL = 2.0   # seconds between metric prints
WINDOW_SECS     = 4.0   # EEG history used per band-power computation

# Ancillary preset (optical/infrared) at 64 Hz
# optical_channels = {1..16} per brainflow_boards.cpp
OPTICAL_CHANNEL = 1     # channel index 1 = first infrared LED

# ── Main ──────────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Muse S Athena BrainFlow test")
    parser.add_argument("--mac",      default="",   help="Bluetooth MAC address, e.g. D4:22:CD:00:AA:BB")
    parser.add_argument("--duration", default=30.0, type=float, help="Test duration in seconds (default 30)")
    args = parser.parse_args()

    params = BrainFlowInputParams()
    if args.mac:
        params.mac_address = args.mac
    else:
        print("[WARN] No MAC address given — attempting auto-discovery (may be slow).")

    board = BoardShim(BOARD_ID, params)
    BoardShim.enable_dev_board_logger()

    # Prepare ML models
    mind_model = MLModel(BrainFlowModelParams(
        BrainFlowMetrics.MINDFULNESS.value, BrainFlowClassifiers.DEFAULT_CLASSIFIER.value))
    rest_model = MLModel(BrainFlowModelParams(
        BrainFlowMetrics.RESTFULNESS.value, BrainFlowClassifiers.DEFAULT_CLASSIFIER.value))

    try:
        print(f"\n[INFO] Connecting to Muse S Athena (board_id={BOARD_ID})...")
        board.prepare_session()
        board.start_stream(45000)   # ring buffer ~2.9 min at 256 Hz
        print("[OK]   Connected and streaming.\n")

        sampling_rate  = BoardShim.get_sampling_rate(BOARD_ID)
        eeg_channels   = BoardShim.get_eeg_channels(BOARD_ID)
        eeg_names      = BoardShim.get_eeg_names(BOARD_ID)   # TP9, AF7, AF8, TP10
        samples_needed = int(sampling_rate * WINDOW_SECS)

        # Optical channels come from the ancillary preset
        anc_preset      = BrainFlowPresets.ANCILLARY_PRESET.value
        anc_rate        = BoardShim.get_sampling_rate(BOARD_ID, anc_preset)

        try:
            optical_channels = BoardShim.get_optical_channels(BOARD_ID, anc_preset)
            has_optical      = len(optical_channels) > 0
        except Exception:
            has_optical = False

        print(f"EEG: {sampling_rate} Hz  channels: {list(zip(eeg_channels, eeg_names))}")
        print(f"Optical (infrared): {anc_rate} Hz  available={has_optical}")
        print(f"\nCollecting data — press Ctrl+C to stop early.\n")
        print(f"{'Elapsed':>8}  {'Mindful':>8}  {'Restful':>8}  {'Stress~':>8}  "
              f"{'Optical':>10}  {'EEG quality':}")
        print("-" * 80)

        mind_model.prepare()
        rest_model.prepare()

        start_time = time.time()
        while time.time() - start_time < args.duration:
            time.sleep(UPDATE_INTERVAL)

            elapsed = time.time() - start_time

            # ── EEG metrics ────────────────────────────────────────────────
            eeg_data = board.get_current_board_data(samples_needed)
            n_cols   = eeg_data.shape[1]

            if n_cols < samples_needed // 2:
                print(f"{elapsed:7.1f}s   [waiting for enough EEG samples ({n_cols}/{samples_needed})]")
                continue

            bands          = DataFilter.get_avg_band_powers(eeg_data, eeg_channels, sampling_rate, True)
            feature_vector = bands[0]   # [delta, theta, alpha, beta, gamma] averages

            mindfulness = mind_model.predict(feature_vector)[0]
            restfulness = rest_model.predict(feature_vector)[0]
            stress      = 1.0 - mindfulness

            # Simple per-channel SNR check: non-NaN and non-zero across all EEG channels
            import numpy as np
            eeg_matrix = eeg_data[eeg_channels, :]
            quality    = "GOOD" if not np.isnan(eeg_matrix).any() and np.std(eeg_matrix) > 1e-6 else "POOR"

            # ── Optical (infrared PPG) ─────────────────────────────────────
            optical_str = "N/A"
            if has_optical:
                try:
                    anc_data = board.get_current_board_data(
                        anc_rate * 4, BrainFlowPresets.ANCILLARY_PRESET.value)
                    ch       = optical_channels[0]
                    optical  = float(np.mean(np.abs(anc_data[ch, :])))
                    optical_str = f"{optical:10.1f}"
                except Exception:
                    pass

            print(f"{elapsed:7.1f}s   {mindfulness:8.3f}  {restfulness:8.3f}  "
                  f"{stress:8.3f}  {optical_str:>10}  {quality}")

        print(f"\n[OK] Test complete ({args.duration:.0f}s).")
        print("If Stress~ values fluctuated above 0.55 you can verify the hint threshold.\n")

    except KeyboardInterrupt:
        print("\n[INFO] Stopped by user.")

    except Exception as e:
        print(f"\n[ERROR] {e}")
        print("\nTroubleshooting:")
        print("  • Ensure bluetooth is on:    rfkill unblock bluetooth")
        print("  • Start the BT daemon:       sudo systemctl start bluetooth")
        print("  • Pair first if needed:      bluetoothctl  →  pair XX:XX:XX:XX:XX:XX")
        print("  • On Arch, ensure bluez is installed:  sudo pacman -S bluez bluez-utils")

    finally:
        try:
            mind_model.release()
            rest_model.release()
        except Exception:
            pass
        try:
            board.stop_stream()
            board.release_session()
            print("[OK] Session released.")
        except Exception:
            pass


if __name__ == "__main__":
    main()
