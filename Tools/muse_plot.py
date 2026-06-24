"""
Muse session plotter  —  draws the three 0..1 cognitive indices over a session, with the
game-state transitions (rest baseline -> active baseline -> gameplay) marked as labelled
vertical lines.

It reads the CSV that Tools/muse_bridge.py writes with `--record`:
    iso_time,t,phase,contact,used,delta,theta,alpha,beta,gamma,theta_z,alpha_z,beta_z,
    stress,attention,cognitive_load

The `phase` column gives the game step at each tick; this script collapses consecutive
ticks of the same phase into spans and draws a vertical line + label at each transition.

USAGE
    # 1) record a session (the bridge):
    python Tools/muse_bridge.py --unity --record            # auto-names Tools/sessions/...
    python Tools/muse_bridge.py --record mysession.csv

    # 2a) final figure from the recorded log:
    python Tools/muse_plot.py Tools/sessions/session_*.csv           # newest match
    python Tools/muse_plot.py mysession.csv --save figures/run1.png  # write PNG, no window

    # 2b) live, while the bridge is still writing the same file (run in another terminal):
    python Tools/muse_plot.py mysession.csv --live

    # also plot the raw band powers underneath (delta..gamma):
    python Tools/muse_plot.py mysession.csv --bands

DEPENDENCIES
    pip install matplotlib numpy
"""

import argparse
import csv
import glob
import os
import sys
import time

try:
    import numpy as np
    import matplotlib
except ImportError as e:
    print(f"[ERROR] Missing dependency: {e}\nRun:  pip install matplotlib numpy")
    sys.exit(1)

INDEX_COLS = ["stress", "attention", "cognitive_load"]
INDEX_LABELS = {"stress": "Stress", "attention": "Attention", "cognitive_load": "Cognitive load"}
INDEX_COLORS = {"stress": "#d62728", "attention": "#1f77b4", "cognitive_load": "#2ca02c"}
BAND_COLS = ["delta", "theta", "alpha", "beta", "gamma"]

# Human labels for the phase names the bridge emits (both the --unity 3-phase flow and the
# standalone single-baseline flow).
PHASE_LABELS = {
    "idle": "Idle",
    "baseline": "Baseline",
    "baseline_rest": "Rest baseline",
    "baseline_active": "Active baseline",
    "active": "Gameplay",
    "streaming": "Gameplay",
}
# Shaded background per phase so the game steps are readable even without the lines.
PHASE_SHADES = {
    "idle": "#f5f5f5",
    "baseline": "#eef3fb",
    "baseline_rest": "#eef3fb",
    "baseline_active": "#fbf3e6",
    "active": "#ffffff",
    "streaming": "#ffffff",
}


def _resolve(path):
    """Accept a literal file, or a glob (then pick the newest match)."""
    if os.path.isfile(path):
        return path
    matches = sorted(glob.glob(path), key=os.path.getmtime)
    if not matches:
        sys.exit(f"[ERROR] no file matches: {path}")
    return matches[-1]


def load(path):
    """Read the CSV -> (t, {col: float-array-with-NaNs}, phase-list). Robust to the file
    still being written (a half-flushed final line is skipped)."""
    t, cols, phases = [], {c: [] for c in INDEX_COLS + BAND_COLS}, []
    with open(path, newline="") as f:
        reader = csv.DictReader(f)
        for r in reader:
            if not r.get("t"):
                continue
            try:
                tv = float(r["t"])
            except ValueError:
                continue                       # partially written line — stop cleanly
            t.append(tv)
            phases.append(r.get("phase", "") or "")
            for c in cols:
                v = r.get(c, "")
                cols[c].append(float(v) if v not in ("", None) else np.nan)
    return (np.array(t), {c: np.array(v, dtype=float) for c, v in cols.items()}, phases)


def phase_spans(t, phases):
    """Collapse consecutive equal phases into [(phase, t_start, t_end), ...]."""
    spans = []
    if not phases:
        return spans
    start_i = 0
    for i in range(1, len(phases) + 1):
        if i == len(phases) or phases[i] != phases[start_i]:
            end_t = t[i - 1] if i == len(phases) else t[i]
            spans.append((phases[start_i], t[start_i], end_t))
            start_i = i
    return spans


def draw_phase_markers(ax, spans, label=True):
    """Shade each phase span and draw a labelled vertical line at every transition."""
    from matplotlib.transforms import blended_transform_factory
    for phase, t0, t1 in spans:
        ax.axvspan(t0, t1, color=PHASE_SHADES.get(phase, "#ffffff"), zorder=0)
    for i, (phase, t0, _t1) in enumerate(spans):
        if i == 0:
            continue                            # no transition before the first span
        ax.axvline(t0, color="#444444", ls="--", lw=1.0, zorder=3)
    if label:
        # x in data coords, y in axes fraction so labels sit just inside the top edge
        # (clear of the title) regardless of the data range.
        tf = blended_transform_factory(ax.transData, ax.transAxes)
        for phase, t0, t1 in spans:
            ax.text((t0 + t1) / 2, 0.98, PHASE_LABELS.get(phase, phase), transform=tf,
                    ha="center", va="top", fontsize=8, color="#333333",
                    bbox=dict(boxstyle="round,pad=0.15", fc="white", ec="none", alpha=0.7))


def render(fig, axes, data, with_bands):
    """(Re)draw the figure in place from freshly loaded data — shared by static & live."""
    t, cols, phases = data
    spans = phase_spans(t, phases)

    ax = axes[0]
    ax.clear()
    for c in INDEX_COLS:
        y = cols[c]
        if np.any(~np.isnan(y)):
            ax.plot(t, y, color=INDEX_COLORS[c], lw=1.6, label=INDEX_LABELS[c])
    ax.axhline(0.5, color="#999999", lw=0.8, ls=":", zorder=1)   # 0.5 == baseline level
    ax.set_ylim(-0.02, 1.02)
    ax.set_ylabel("index (0–1)")
    ax.set_title("Muse cognitive indices across game steps")
    ax.legend(loc="center left", bbox_to_anchor=(1.01, 0.5), fontsize=8, framealpha=0.9)
    draw_phase_markers(ax, spans, label=True)

    if with_bands:
        axb = axes[1]
        axb.clear()
        for c in BAND_COLS:
            y = cols[c]
            if np.any(~np.isnan(y)):
                axb.plot(t, y, lw=1.2, label=c)
        axb.set_ylabel("band power (µV²)")
        axb.set_yscale("log")
        axb.legend(loc="center left", bbox_to_anchor=(1.01, 0.5), fontsize=8, framealpha=0.9)
        draw_phase_markers(axb, spans, label=False)

    axes[-1].set_xlabel("time (s)")
    fig.tight_layout()


def make_figure(with_bands):
    import matplotlib.pyplot as plt
    n = 2 if with_bands else 1
    fig, axes = plt.subplots(n, 1, figsize=(11, 4 * n), sharex=True, squeeze=False)
    return fig, [a[0] for a in axes]


def main():
    ap = argparse.ArgumentParser(description="Plot a recorded Muse session.")
    ap.add_argument("csv", help="CSV written by muse_bridge.py --record (file or glob).")
    ap.add_argument("--bands", action="store_true",
                    help="Add a second panel with the raw delta..gamma band powers.")
    ap.add_argument("--save", metavar="PNG",
                    help="Write the figure to this path instead of showing a window.")
    ap.add_argument("--live", action="store_true",
                    help="Keep re-reading the CSV and redrawing (use while the bridge runs).")
    ap.add_argument("--interval", type=float, default=1.0,
                    help="Live redraw interval in seconds (default 1).")
    args = ap.parse_args()

    if args.save and not args.live:
        matplotlib.use("Agg")                   # headless render, no display needed
    import matplotlib.pyplot as plt

    path = _resolve(args.csv)
    fig, axes = make_figure(args.bands)

    if args.live:
        plt.ion()
        fig.show()
        print(f"[INFO] live-plotting {path} (Ctrl+C to stop)")
        try:
            while True:
                render(fig, axes, load(path), args.bands)
                fig.canvas.draw_idle()
                fig.canvas.flush_events()
                plt.pause(args.interval)
        except KeyboardInterrupt:
            print("\n[INFO] stopped.")
        return

    render(fig, axes, load(path), args.bands)
    if args.save:
        os.makedirs(os.path.dirname(os.path.abspath(args.save)), exist_ok=True)
        fig.savefig(args.save, dpi=150)
        print(f"[OK] wrote {args.save}")
    else:
        plt.show()


if __name__ == "__main__":
    main()
