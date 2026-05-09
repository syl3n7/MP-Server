"""
load_timeseries.py — Plot connected/authenticated players and active rooms over time.

Reads the Serilog CLEF heartbeat events (emitted every 30 s) and produces:
  - sessions_over_time.png  : total vs authenticated sessions
  - rooms_over_time.png     : active room count
  - churn_over_time.png     : inactive sessions removed per heartbeat tick

Usage:
    cd analysis/
    pip install -r requirements.txt
    python load_timeseries.py [--logs ../logs] [--out ./plots]
"""

import argparse
from pathlib import Path

import matplotlib.pyplot as plt
import matplotlib.dates as mdates
import seaborn as sns
import pandas as pd

from parse_logs import load_logs, heartbeats


def plot_sessions(hb: pd.DataFrame, out: Path) -> None:
    fig, ax = plt.subplots(figsize=(12, 5))
    ax.plot(hb["timestamp"], hb["ActiveSessions"],       label="Total sessions",         linewidth=2)
    ax.plot(hb["timestamp"], hb["AuthenticatedSessions"], label="Authenticated sessions", linewidth=2, linestyle="--")
    ax.xaxis.set_major_formatter(mdates.DateFormatter("%H:%M"))
    ax.xaxis.set_major_locator(mdates.AutoDateLocator())
    fig.autofmt_xdate()
    ax.set_title("Player Sessions Over Time")
    ax.set_xlabel("Time (UTC)")
    ax.set_ylabel("Count")
    ax.legend()
    ax.grid(True, alpha=0.3)
    fig.tight_layout()
    dest = out / "sessions_over_time.png"
    fig.savefig(dest, dpi=150)
    plt.close(fig)
    print(f"  Saved: {dest}")


def plot_rooms(hb: pd.DataFrame, out: Path) -> None:
    fig, ax = plt.subplots(figsize=(12, 4))
    ax.fill_between(hb["timestamp"], hb["ActiveRooms"], alpha=0.4, label="Active rooms")
    ax.plot(hb["timestamp"], hb["ActiveRooms"], linewidth=2)
    ax.xaxis.set_major_formatter(mdates.DateFormatter("%H:%M"))
    ax.xaxis.set_major_locator(mdates.AutoDateLocator())
    fig.autofmt_xdate()
    ax.set_title("Active Rooms Over Time")
    ax.set_xlabel("Time (UTC)")
    ax.set_ylabel("Rooms")
    ax.grid(True, alpha=0.3)
    fig.tight_layout()
    dest = out / "rooms_over_time.png"
    fig.savefig(dest, dpi=150)
    plt.close(fig)
    print(f"  Saved: {dest}")


def plot_churn(hb: pd.DataFrame, out: Path) -> None:
    fig, ax = plt.subplots(figsize=(12, 4))
    ax.bar(hb["timestamp"], hb["InactiveSessions"], width=0.0003, label="Removed (inactive)")
    ax.xaxis.set_major_formatter(mdates.DateFormatter("%H:%M"))
    ax.xaxis.set_major_locator(mdates.AutoDateLocator())
    fig.autofmt_xdate()
    ax.set_title("Session Churn (Inactive Removals) Over Time")
    ax.set_xlabel("Time (UTC)")
    ax.set_ylabel("Sessions removed")
    ax.grid(True, alpha=0.3)
    fig.tight_layout()
    dest = out / "churn_over_time.png"
    fig.savefig(dest, dpi=150)
    plt.close(fig)
    print(f"  Saved: {dest}")


def main():
    parser = argparse.ArgumentParser(description="Plot MP-Server load time-series from Serilog CLEF logs")
    parser.add_argument("--logs", default="../logs", help="Directory containing mp-server-*.log files")
    parser.add_argument("--out",  default="./plots", help="Output directory for PNG files")
    args = parser.parse_args()

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)

    print(f"Loading logs from: {Path(args.logs).resolve()}")
    df = load_logs(args.logs)
    hb = heartbeats(df)

    if hb.empty:
        print("No heartbeat rows found — run the server for at least 30 seconds first.")
        return

    print(f"Found {len(hb)} heartbeat samples spanning "
          f"{hb['timestamp'].min().strftime('%H:%M')} – {hb['timestamp'].max().strftime('%H:%M')} UTC")

    sns.set_theme(style="whitegrid")
    plot_sessions(hb, out)
    plot_rooms(hb, out)
    plot_churn(hb, out)
    print("Done.")


if __name__ == "__main__":
    main()
