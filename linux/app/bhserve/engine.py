"""EngineClient — drives the bash engine (engine/bhserve) the same way the macOS
SwiftUI app and the Windows WinUI app drive their cores: spawn the CLI, parse the
`api` JSON, run verbs. Long operations run on a worker thread and report back on the
GTK main loop via GLib.idle_add.
"""
from __future__ import annotations

import json
import os
import re
import shutil
import subprocess
import threading
from typing import Callable

# Strip terminal colour/escape sequences from engine output before it's shown in toasts /
# the activity log / parsed (the bash engine emits ANSI for its ✓/✗/headers).
_ANSI = re.compile(r"\x1b\[[0-9;]*[A-Za-z]")

import gi
from gi.repository import GLib

# Verbs that touch root-owned state (apt, systemd, :80/:443, /etc/hosts, mkcert CA). The GUI
# runs these via pkexec (a single polkit prompt); the engine then runs root-aware and chowns
# anything it creates back to the user. Everything else (api/status/logs/config/db/php) runs
# unprivileged as the user.
_PRIVILEGED = {"install", "update", "uninstall", "start", "stop", "restart",
               "secure", "dns", "helper", "loginitem", "pma", "adminer", "mailpit"}


def _needs_root(args: tuple) -> bool:
    if not args:
        return False
    v = args[0]
    if v in _PRIVILEGED:
        return True
    if v == "site" and len(args) > 1 and args[1] in ("add", "rm", "remove"):
        return True
    if v in ("pysite", "nodesite") and len(args) > 1 and args[1] in ("add", "rm", "remove"):
        return True
    return False


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

    # ── command construction (elevate privileged verbs) ──────────────────────
    _sudo_nopw: bool | None = None

    def _can_sudo_nopw(self) -> bool:
        """True if `sudo` runs without a password (passwordless sudoers / WSL). Cached."""
        if EngineClient._sudo_nopw is None:
            try:
                EngineClient._sudo_nopw = (shutil.which("sudo") is not None and
                    subprocess.run(["sudo", "-n", "true"], capture_output=True, timeout=5).returncode == 0)
            except Exception:
                EngineClient._sudo_nopw = False
        return EngineClient._sudo_nopw

    def _build(self, args: tuple) -> list[str]:
        base = ["bash", self.path, *args]
        if not (_needs_root(args) and os.geteuid() != 0):
            return base
        # 1) passwordless sudo (WSL, or our nginx helper / a configured sudoers) → silent, and
        #    works where there's no polkit auth agent (e.g. WSL2 has no agent to show a prompt).
        if self._can_sudo_nopw():
            return ["sudo", "-E", "bash", self.path, *args]
        # 2) desktop: pkexec shows a polkit password dialog (GNOME/KDE).
        if shutil.which("pkexec"):
            bh_home = os.environ.get("BHSERVE_HOME", os.path.expanduser("~/.bhserve"))
            return ["pkexec", "env", f"BHSERVE_HOME={bh_home}", "BHSERVE_GUI=1",
                    "bash", self.path, *args]
        return base

    # ── synchronous run (use only for fast verbs like api/status) ────────────
    def run(self, *args: str, timeout: int | None = None) -> tuple[int, str]:
        try:
            p = subprocess.run(
                self._build(args),
                capture_output=True, text=True, timeout=timeout,
                env={**os.environ, "BHSERVE_GUI": "1"},
            )
            # pkexec exit 126 = user dismissed the auth dialog; 127 = not authorized.
            if p.returncode in (126, 127) and _needs_root(args):
                return p.returncode, "Cancelled — administrator approval is required for this action."
            return p.returncode, _ANSI.sub("", (p.stdout or "") + (p.stderr or ""))
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
