"""
Shared log parser for MP-Server Serilog CLEF files.

CLEF (Compact Log Event Format) — one JSON object per line:
  {"@t": "2026-05-09T...", "@mt": "...", "@l": "Information", "Prop": value, ...}

Usage:
    from parse_logs import load_logs, heartbeats, events
"""

import json
from pathlib import Path
from datetime import datetime, timezone

import pandas as pd


def _parse_timestamp(raw: str) -> datetime:
    # CLEF timestamps are ISO-8601; Python 3.11+ fromisoformat handles Z, older needs replace
    raw = raw.replace("Z", "+00:00")
    return datetime.fromisoformat(raw)


def load_logs(log_dir: str = "../logs") -> pd.DataFrame:
    """
    Read all mp-server-*.log CLEF files from log_dir and return a single DataFrame.
    Each row is one log event with at minimum: timestamp, level, message_template, and
    any structured properties present on the event.
    """
    rows = []
    path = Path(log_dir)
    files = sorted(path.glob("mp-server-*.log"))

    if not files:
        raise FileNotFoundError(
            f"No log files found in '{path.resolve()}'. "
            "Run the server first to generate logs."
        )

    for f in files:
        with f.open(encoding="utf-8") as fh:
            for lineno, line in enumerate(fh, 1):
                line = line.strip()
                if not line:
                    continue
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError as exc:
                    print(f"  [WARN] {f.name}:{lineno} — skipping malformed line: {exc}")
                    continue

                row = {
                    "timestamp": _parse_timestamp(obj.get("@t", "")),
                    "level":     obj.get("@l", "Information"),
                    "template":  obj.get("@mt", ""),
                }
                # Merge every non-@ key as a structured property
                for k, v in obj.items():
                    if not k.startswith("@"):
                        row[k] = v

                rows.append(row)

    df = pd.DataFrame(rows)
    df["timestamp"] = pd.to_datetime(df["timestamp"], utc=True)
    df.sort_values("timestamp", inplace=True)
    df.reset_index(drop=True, inplace=True)
    return df


def heartbeats(df: pd.DataFrame) -> pd.DataFrame:
    """Filter to Heartbeat rows that carry load metrics."""
    mask = df["template"].str.contains("Heartbeat:", na=False)
    cols = ["timestamp", "ActiveSessions", "AuthenticatedSessions", "ActiveRooms", "InactiveSessions"]
    present = [c for c in cols if c in df.columns]
    return df.loc[mask, present].copy()


def events(df: pd.DataFrame, *templates: str) -> pd.DataFrame:
    """
    Filter to rows whose message template contains any of the given substrings.
    Example:
        events(df, "REGISTER", "LOGIN", "AUTO_AUTH")
    """
    if not templates:
        return df
    mask = df["template"].str.contains("|".join(templates), na=False)
    return df.loc[mask].copy()
