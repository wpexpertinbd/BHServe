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
        # --background (login autostart): first activation keeps the window hidden so only
        # the tray icon appears. Cleared after the first activate, so a later tray "Open" /
        # second launch presents the window normally.
        self._bg_start = False
        # A hidden (never-presented or closed-to-tray) window does NOT keep the GLib main
        # loop alive — without an explicit hold the app exits as soon as activate returns.
        self._held = False

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
        if self._bg_start and not wins:
            # Login autostart: stay hidden — the tray icon is the only UI. If the tray can't
            # appear (no AppIndicator extension), show the window instead of an invisible app.
            self._bg_start = False
            win.set_visible(False)
            self._hold()
            GLib.timeout_add_seconds(6, self._bg_fallback, win)
            return
        win.set_visible(True)
        win.present()
        self._unhold()

    def _bg_fallback(self, win) -> bool:
        if not self._tray_running() and not win.get_visible():
            win.set_visible(True)
            win.present()
            self._unhold()
        return False

    # ── keep-alive while no window is visible (background start / closed-to-tray) ──
    def _hold(self) -> None:
        if not self._held:
            self._held = True
            self.hold()

    def _unhold(self) -> None:
        if self._held:
            self._held = False
            self.release()

    # ── close-to-tray ──
    def _on_close(self, win) -> bool:
        # With a live tray icon, closing the window just hides it (BHServe stays reachable);
        # otherwise fall through to the default (destroy → quit). Hold the app while hidden
        # so the main loop can't decide it has nothing left to do.
        if self._tray_running():
            win.set_visible(False)
            self._hold()
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
    import sys
    app = BHServeApp()
    # --background/--tray: launched by the login autostart entry — start hidden-to-tray.
    app._bg_start = ("--background" in sys.argv) or ("--tray" in sys.argv)
    # Ensure the data dir exists BEFORE any GTK/window/api machinery — a fresh install is otherwise
    # "not initialized" (blank Services, nothing installable). Plain Python, idempotent, no root.
    try:
        app.engine.run("init", timeout=20)
    except Exception:  # noqa: BLE001
        pass
    return app.run([sys.argv[0]])   # strip our flags — GTK would reject unknown options
