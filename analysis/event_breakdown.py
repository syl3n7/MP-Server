"""
event_breakdown.py — Bar/pie charts of auth events, room lifecycle, and errors.

Reads all structured log events from the Serilog CLEF files and produces:
  - auth_events.png       : REGISTER / LOGIN / AUTO_AUTH counts (success vs failure)
  - room_lifecycle.png    : CREATE_ROOM / JOIN / LEAVE / START_GAME / REMOVED counts
  - error_breakdown.png   : Top error message templates

Usage:
    cd analysis/
    pip install -r requirements.txt
    python event_breakdown.py [--logs ../logs] [--out ./plots]
"""

import argparse
from pathlib import Path
from collections import Counter

import matplotlib.pyplot as plt
import seaborn as sns
import pandas as pd

from parse_logs import load_logs, events


# ── Helpers ───────────────────────────────────────────────────────────────────

def _count_bar(counts: dict, title: str, xlabel: str, dest: Path, color=None) -> None:
    if not counts:
        print(f"  [SKIP] No data for: {title}")
        return
    labels = list(counts.keys())
    values = list(counts.values())
    fig, ax = plt.subplots(figsize=(max(6, len(labels) * 1.4), 5))
    bars = ax.bar(labels, values, color=color)
    ax.bar_label(bars, padding=3)
    ax.set_title(title)
    ax.set_xlabel(xlabel)
    ax.set_ylabel("Count")
    ax.grid(axis="y", alpha=0.3)
    fig.tight_layout()
    fig.savefig(dest, dpi=150)
    plt.close(fig)
    print(f"  Saved: {dest}")


# ── Auth events ───────────────────────────────────────────────────────────────

def plot_auth(df: pd.DataFrame, out: Path) -> None:
    buckets = {
        "REGISTER OK":   events(df, "REGISTER →"),
        "LOGIN OK":      events(df, "LOGIN →"),
        "AUTO_AUTH OK":  events(df, "AUTO_AUTH →"),
        "REGISTER FAIL": events(df, "REGISTER_FAILED"),
        "LOGIN FAIL":    events(df, "LOGIN_FAILED"),
        "AUTH FAIL":     events(df, "AUTO_AUTH_FAILED"),
    }
    counts = {k: len(v) for k, v in buckets.items() if len(v) > 0}
    colors = ["#4caf50" if "OK" in k else "#f44336" for k in counts]
    _count_bar(counts, "Auth Events", "Event type", out / "auth_events.png", color=colors)


# ── Room lifecycle ─────────────────────────────────────────────────────────────

def plot_rooms(df: pd.DataFrame, out: Path) -> None:
    buckets = {
        "Room Created":  events(df, "created room"),
        "Player Joined": events(df, "joined room"),
        "Player Left":   events(df, "left room", "LEAVE_ROOM"),
        "Game Started":  events(df, "GAME_STARTED", "game started"),
        "Room Removed":  events(df, "removed", "Room removed"),
    }
    counts = {k: len(v) for k, v in buckets.items() if len(v) > 0}
    _count_bar(counts, "Room Lifecycle Events", "Event type", out / "room_lifecycle.png")


# ── Error breakdown ───────────────────────────────────────────────────────────

def plot_errors(df: pd.DataFrame, out: Path) -> None:
    err_df = df[df["level"].isin(["Error", "Warning"])].copy()
    if err_df.empty:
        print("  [SKIP] No error/warning rows found.")
        return

    # Shorten templates for display
    top = Counter(err_df["template"].tolist()).most_common(10)
    labels = [t[:60] + ("…" if len(t) > 60 else "") for t, _ in top]
    values = [c for _, c in top]

    fig, ax = plt.subplots(figsize=(10, max(4, len(labels) * 0.5)))
    bars = ax.barh(labels[::-1], values[::-1], color="#ef5350")
    ax.bar_label(bars, padding=3)
    ax.set_title("Top Error / Warning Templates")
    ax.set_xlabel("Count")
    ax.grid(axis="x", alpha=0.3)
    fig.tight_layout()
    dest = out / "error_breakdown.png"
    fig.savefig(dest, dpi=150)
    plt.close(fig)
    print(f"  Saved: {dest}")


# ── Entry point ───────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(description="Plot MP-Server event breakdown from Serilog CLEF logs")
    parser.add_argument("--logs", default="../logs", help="Directory containing mp-server-*.log files")
    parser.add_argument("--out",  default="./plots", help="Output directory for PNG files")
    args = parser.parse_args()

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    print(f"Loading logs from: {Path(args.logs).resolve()}")
    df = load_logs(args.logs)
    print(f"Total events loaded: {len(df)}")

    sns.set_theme(style="whitegrid")
    plot_auth(df, out)
    plot_rooms(df, out)
    plot_errors(df, out)
    print("Done.")


if __name__ == "__main__":
    main()
