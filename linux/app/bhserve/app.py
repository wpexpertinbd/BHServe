"""BHServeApp — the Adw.Application: loads brand CSS, opens the MainWindow."""
from __future__ import annotations

import os
import shutil
import subprocess

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, Gdk, Gio, GLib, Gtk  # noqa: E402

from .engine import EngineClient  # noqa: E402
from .window import MainWindow  # noqa: E402


class BHServeApp(Adw.Application):
    def __init__(self) -> None:
        super().__init__(application_id="com.biswashost.bhserve",
                         flags=Gio.ApplicationFlags.DEFAULT_FLAGS)
        self.engine = EngineClient()
        self._tray_proc = None

    def do_startup(self) -> None:
        Adw.Application.do_startup(self)
        GLib.set_application_name("BHServe")
        self._load_css()
        # "quit" action the tray helper invokes over D-Bus (org.freedesktop.Application).
        act = Gio.SimpleAction.new("quit", None)
        act.connect("activate", lambda *_: self._quit_all())
        self.add_action(act)
        self._start_tray()

    def do_activate(self) -> None:
        wins = self.get_windows()          # includes a hidden (closed-to-tray) window
        if wins:
            win = wins[0]
        else:
            win = MainWindow(self)
            win.connect("close-request", self._on_close)
        win.set_visible(True)
        win.present()

    # ── close-to-tray ──
    def _on_close(self, win) -> bool:
        # With a live tray icon, closing the window just hides it (BHServe stays reachable);
        # otherwise fall through to the default (destroy → quit).
        if self._tray_running():
            win.set_visible(False)
            return True
        return False

    def _tray_path(self):
        here = os.path.dirname(os.path.abspath(__file__))  # …/bhserve
        for c in (os.path.join(here, "..", "bin", "bhserve-tray"),
                  "/usr/lib/bhserve/app/bin/bhserve-tray"):
            if os.path.exists(c):
                return os.path.abspath(c)
        return shutil.which("bhserve-tray")

    def _start_tray(self) -> None:
        path = self._tray_path()
        if not path:
            return
        try:
            self._tray_proc = subprocess.Popen(["python3", path, str(os.getpid())])
        except Exception:  # noqa: BLE001
            self._tray_proc = None

    def _tray_running(self) -> bool:
        return self._tray_proc is not None and self._tray_proc.poll() is None

    def _quit_all(self) -> None:
        if self._tray_proc and self._tray_proc.poll() is None:
            try:
                self._tray_proc.terminate()
            except Exception:  # noqa: BLE001
                pass
        self.quit()

    def _load_css(self) -> None:
        css = os.path.join(os.path.dirname(os.path.abspath(__file__)), "style.css")
        if not os.path.exists(css):
            return
        provider = Gtk.CssProvider()
        provider.load_from_path(css)
        Gtk.StyleContext.add_provider_for_display(
            Gdk.Display.get_default(), provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION
        )


def main() -> int:
    app = BHServeApp()
    # Ensure the data dir exists BEFORE any GTK/window/api machinery — a fresh install is otherwise
    # "not initialized" (blank Services, nothing installable). Plain Python, idempotent, no root.
    try:
        app.engine.run("init", timeout=20)
    except Exception:  # noqa: BLE001
        pass
    return app.run(None)
