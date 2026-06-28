"""Live system metrics from /proc (no extra deps), matching the macOS/Windows dashboard:
CPU% + sparkline history, memory, disk, and network throughput. CPU and network are rate
measurements, so their samplers hold the previous reading between dashboard refreshes.
"""
from __future__ import annotations

import os
import time


class CpuSampler:
    def __init__(self) -> None:
        self._last = self._read()

    @staticmethod
    def _read() -> tuple[int, int]:
        with open("/proc/stat") as f:
            vals = [int(x) for x in f.readline().split()[1:]]
        idle = vals[3] + (vals[4] if len(vals) > 4 else 0)
        return sum(vals), idle

    def percent(self) -> float:
        total, idle = self._read()
        lt, li = self._last
        self._last = (total, idle)
        dt, di = total - lt, idle - li
        if dt <= 0:
            return 0.0
        return max(0.0, min(100.0, 100.0 * (dt - di) / dt))


class NetSampler:
    def __init__(self) -> None:
        self._t = time.monotonic()
        self._rx, self._tx = self._read()

    @staticmethod
    def _read() -> tuple[int, int]:
        rx = tx = 0
        try:
            with open("/proc/net/dev") as f:
                for line in f.readlines()[2:]:
                    iface, _, data = line.partition(":")
                    if iface.strip() == "lo":
                        continue
                    cols = data.split()
                    rx += int(cols[0])
                    tx += int(cols[8])
        except Exception:
            pass
        return rx, tx

    def rate_kbps(self) -> tuple[float, float]:
        now = time.monotonic()
        rx, tx = self._read()
        dt = (now - self._t) or 1.0
        down = (rx - self._rx) / dt / 1024.0
        up = (tx - self._tx) / dt / 1024.0
        self._t = now
        self._rx, self._tx = rx, tx
        return max(0.0, down), max(0.0, up)


def memory() -> tuple[float, float, float]:
    """(used_GB, total_GB, used_percent)."""
    info: dict[str, int] = {}
    try:
        for line in open("/proc/meminfo"):
            k, _, v = line.partition(":")
            info[k] = int(v.strip().split()[0])
    except Exception:
        return 0.0, 0.0, 0.0
    total = info.get("MemTotal", 0) / 1024 / 1024
    avail = info.get("MemAvailable", 0) / 1024 / 1024
    used = max(0.0, total - avail)
    return used, total, (100 * used / total if total else 0.0)


def disk() -> tuple[float, float, float]:
    """(used_GB, total_GB, used_percent) for the user's home filesystem."""
    try:
        st = os.statvfs(os.path.expanduser("~"))
    except Exception:
        return 0.0, 0.0, 0.0
    total = st.f_blocks * st.f_frsize / 1e9
    free = st.f_bfree * st.f_frsize / 1e9
    used = max(0.0, total - free)
    return used, total, (100 * used / total if total else 0.0)


def rate_str(kbps: float) -> str:
    return f"{kbps / 1024:.1f} MB/s" if kbps >= 1024 else f"{kbps:.0f} KB/s"
