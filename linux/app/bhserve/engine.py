"""EngineClient — drives the bash engine (engine/bhserve) the same way the macOS
SwiftUI app and the Windows WinUI app drive their cores: spawn the CLI, parse the
`api` JSON, run verbs. Long operations run on a worker thread and report back on the
GTK main loop via GLib.idle_add.
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import threading
from typing import Callable

import gi
from gi.repository import GLib


class EngineClient:
    def __init__(self) -> None:
        self.path = self._find_engine()

    # ── locating the engine script ───────────────────────────────────────────
    def _find_engine(self) -> str:
        env = os.environ.get("BHSERVE_ENGINE")
        if env and os.path.exists(env):
            return os.path.abspath(env)
        here = os.path.dirname(os.path.abspath(__file__))
        candidates = [
            # repo layout: linux/app/bhserve/engine.py → ../../../engine/bhserve
            os.path.join(here, "..", "..", "..", "engine", "bhserve"),
            os.path.expanduser("~/eng/bhserve"),          # dev sandbox
            "/usr/lib/bhserve/engine/bhserve",            # .deb install
            "/opt/bhserve/engine/bhserve",                # AppImage / opt install
            shutil.which("bhserve"),
        ]
        for c in candidates:
            if c and os.path.exists(c):
                return os.path.abspath(c)
        return "bhserve"  # last resort: rely on PATH

    # ── synchronous run (use only for fast verbs like api/status) ────────────
    def run(self, *args: str, timeout: int | None = None) -> tuple[int, str]:
        try:
            p = subprocess.run(
                ["bash", self.path, *args],
                capture_output=True, text=True, timeout=timeout,
                env={**os.environ, "BHSERVE_GUI": "1"},
            )
            return p.returncode, (p.stdout or "") + (p.stderr or "")
        except subprocess.TimeoutExpired:
            return 1, f"timed out after {timeout}s"
        except Exception as e:  # noqa: BLE001
            return 1, str(e)

    # ── api JSON snapshot the GUI renders from ───────────────────────────────
    def api(self) -> dict:
        rc, out = self.run("api", timeout=20)
        return _extract_json(out)

    # ── async run for anything slow (install/start/secure/…) ─────────────────
    def run_async(self, args: list[str], on_done: Callable[[int, str], None]) -> None:
        def worker() -> None:
            rc, out = self.run(*args)
            GLib.idle_add(on_done, rc, out)
        threading.Thread(target=worker, daemon=True).start()


def _extract_json(text: str) -> dict:
    """The api verb prints pure JSON, but be defensive about any stray header/log
    line by slicing from the first '{' to the matching last '}'."""
    if not text:
        return {}
    try:
        return json.loads(text)
    except Exception:
        pass
    s, e = text.find("{"), text.rfind("}")
    if s >= 0 and e > s:
        try:
            return json.loads(text[s : e + 1])
        except Exception:
            return {}
    return {}
