"""BHServeApp — the Adw.Application: loads brand CSS, opens the MainWindow."""
from __future__ import annotations

import os

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

    def do_startup(self) -> None:
        Adw.Application.do_startup(self)
        GLib.set_application_name("BHServe")
        self._load_css()

    def do_activate(self) -> None:
        win = self.props.active_window
        if not win:
            win = MainWindow(self)
        win.present()

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
