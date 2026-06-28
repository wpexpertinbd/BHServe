"""MainWindow — the Adw split-view shell: an 8-item sidebar, a content stack of the 8
panes, a toast overlay, and a 4-second `bhserve api` refresh loop. It also hosts the
helpers the pages call: run_verb (async verb + toast + refresh), confirm/choose dialogs,
the add-site / create-database dialogs, and the GUI-prefs store.
"""
from __future__ import annotations

import json
import os
import threading

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, GLib, Gtk  # noqa: E402

from . import pages as P  # noqa: E402

GUI_CFG = os.path.expanduser("~/.bhserve/config/gui.json")

NAV = [
    ("dashboard", "Dashboard", "go-home-symbolic", P.DashboardPage),
    ("services", "Services", "applications-system-symbolic", P.ServicesPage),
    ("sites", "Sites", "web-browser-symbolic", P.SitesPage),
    ("databases", "Databases", "drive-harddisk-symbolic", P.DatabasesPage),
    ("node", "Node", "application-x-addon-symbolic", P.NodePage),
    ("python", "Python", "application-x-executable-symbolic", P.PythonPage),
    ("logs", "Logs", "text-x-generic-symbolic", P.LogsPage),
    ("settings", "Settings", "preferences-system-symbolic", P.SettingsPage),
]


class MainWindow(Adw.ApplicationWindow):
    def __init__(self, app) -> None:
        super().__init__(application=app, title="BHServe", default_width=1080, default_height=720)
        self.engine = app.engine
        self.last_data: dict = {}
        self.pages: dict = {}

        self.toast_overlay = Adw.ToastOverlay()
        split = Adw.NavigationSplitView()
        self.toast_overlay.set_child(split)
        self.set_content(self.toast_overlay)

        # ── sidebar ──
        sidebar_tv = Adw.ToolbarView()
        sb_header = Adw.HeaderBar(show_title=False)
        brand = Gtk.Box(spacing=6)
        dot = Gtk.Label(label="●", css_classes=["bh-brand-dot"])
        brand.append(dot)
        brand.append(Gtk.Label(label="BHServe", css_classes=["bh-brand"]))
        sb_header.set_title_widget(brand)
        self.spinner = Gtk.Spinner()
        sb_header.pack_end(self.spinner)
        sidebar_tv.add_top_bar(sb_header)

        self.sidebar_list = Gtk.ListBox(css_classes=["navigation-sidebar"])
        self.sidebar_list.connect("row-selected", self._on_nav)
        for key, label, icon, _cls in NAV:
            row = Gtk.ListBoxRow()
            b = Gtk.Box(spacing=12, margin_top=8, margin_bottom=8, margin_start=8, margin_end=8)
            b.append(Gtk.Image.new_from_icon_name(icon))
            b.append(Gtk.Label(label=label, xalign=0))
            row.set_child(b)
            row.nav_key = key
            self.sidebar_list.append(row)
        sb_scroll = Gtk.ScrolledWindow(vexpand=True)
        sb_scroll.set_child(self.sidebar_list)
        sidebar_tv.set_content(sb_scroll)
        sidebar_page = Adw.NavigationPage(title="BHServe", child=sidebar_tv)
        sidebar_page.set_tag("sidebar")
        split.set_sidebar(sidebar_page)
        split.set_min_sidebar_width(220)
        split.set_max_sidebar_width(260)

        # ── content ──
        content_tv = Adw.ToolbarView()
        self.content_header = Adw.HeaderBar()
        self.content_title = Adw.WindowTitle(title="Dashboard", subtitle="")
        self.content_header.set_title_widget(self.content_title)
        refresh_btn = Gtk.Button(icon_name="view-refresh-symbolic", tooltip_text="Refresh")
        refresh_btn.connect("clicked", lambda *_: self.refresh())
        self.content_header.pack_end(refresh_btn)
        content_tv.add_top_bar(self.content_header)

        self.stack = Gtk.Stack(transition_type=Gtk.StackTransitionType.CROSSFADE)
        for key, _label, _icon, cls in NAV:
            page = cls(self)
            self.pages[key] = page
            self.stack.add_named(page, key)
        content_tv.set_content(self.stack)
        content_page = Adw.NavigationPage(title="Dashboard", child=content_tv)
        split.set_content(content_page)
        self._content_nav = content_page

        self.sidebar_list.select_row(self.sidebar_list.get_row_at_index(0))
        self.refresh()
        GLib.timeout_add_seconds(4, self._tick)

    # ── navigation ──
    def _on_nav(self, _list, row) -> None:
        if not row:
            return
        key = row.nav_key
        self.stack.set_visible_child_name(key)
        label = next(l for k, l, _i, _c in NAV if k == key)
        self.content_title.set_title(label)
        self._content_nav.set_title(label)
        page = self.pages[key]
        if hasattr(page, "refresh") and self.last_data:
            page.refresh(self.last_data)

    # ── api refresh loop ──
    def _tick(self) -> bool:
        self.refresh()
        return True

    def refresh(self) -> None:
        def worker():
            data = self.engine.api()
            GLib.idle_add(self._apply, data)
        threading.Thread(target=worker, daemon=True).start()

    def _apply(self, data: dict) -> bool:
        if data:
            self.last_data = data
        key = self.stack.get_visible_child_name()
        page = self.pages.get(key)
        if page and hasattr(page, "refresh"):
            try:
                page.refresh(self.last_data)
            except Exception as e:  # noqa: BLE001
                print("refresh error:", e)
        return False

    # ── verb runner ──
    def run_verb(self, args, msg, refresh=True) -> None:
        if msg:
            self.toast(msg)
        self.spinner.start()

        def done(rc, out):
            self.spinner.stop()
            if rc != 0:
                self.toast(_first_line(out) or f"{' '.join(args)} failed")
            elif msg:
                self.toast(msg.replace("…", " — done"))
            if refresh:
                self.refresh()

        self.engine.run_async(list(args), done)

    def toast(self, text: str) -> None:
        self.toast_overlay.add_toast(Adw.Toast(title=text, timeout=3))

    # ── dialogs ──
    def confirm(self, title, body, on_ok) -> None:
        dlg = Adw.MessageDialog(transient_for=self, heading=title, body=body)
        dlg.add_response("cancel", "Cancel")
        dlg.add_response("ok", "Continue")
        dlg.set_response_appearance("ok", Adw.ResponseAppearance.DESTRUCTIVE)
        dlg.set_default_response("cancel")
        dlg.connect("response", lambda d, r: on_ok() if r == "ok" else None)
        dlg.present()

    def choose(self, title, body, options, on_pick) -> None:
        if not options:
            self.toast("No options available")
            return
        dlg = Adw.MessageDialog(transient_for=self, heading=title, body=body)
        dd = Gtk.DropDown.new_from_strings(options)
        dlg.set_extra_child(dd)
        dlg.add_response("cancel", "Cancel")
        dlg.add_response("ok", "Apply")
        dlg.set_response_appearance("ok", Adw.ResponseAppearance.SUGGESTED)
        dlg.connect("response", lambda d, r: on_pick(options[dd.get_selected()]) if r == "ok" else None)
        dlg.present()

    def add_site_dialog(self, default_type="wordpress") -> None:
        if default_type in ("node", "py"):
            return self._app_dialog(default_type)
        dlg = Adw.MessageDialog(transient_for=self, heading="Add a website",
                                body="Creates the site folder, vhost and *.test domain.")
        form = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        name = Gtk.Entry(placeholder_text="site name (e.g. myshop)")
        typ = Gtk.DropDown.new_from_strings(["wordpress", "php", "others"])
        php = Gtk.DropDown.new_from_strings([k.replace("php@", "") for k in P.PHP_KEYS])
        srv = Gtk.DropDown.new_from_strings(["nginx", "apache"])
        ssl = Gtk.CheckButton(label="Enable trusted HTTPS (mkcert)", active=True)
        for w, lab in ((name, "Name"), (typ, "Type"), (php, "PHP"), (srv, "Web server")):
            row = Gtk.Box(spacing=10)
            row.append(Gtk.Label(label=lab, width_chars=10, xalign=0))
            w.set_hexpand(True)
            row.append(w)
            form.append(row)
        form.append(ssl)
        dlg.set_extra_child(form)
        dlg.add_response("cancel", "Cancel")
        dlg.add_response("ok", "Create")
        dlg.set_response_appearance("ok", Adw.ResponseAppearance.SUGGESTED)

        def resp(d, r):
            if r != "ok":
                return
            nm = name.get_text().strip()
            if not nm:
                self.toast("Enter a site name")
                return
            args = ["site", "add", nm,
                    "--type", ["wordpress", "php", "others"][typ.get_selected()],
                    "--php", [k.replace("php@", "") for k in P.PHP_KEYS][php.get_selected()],
                    "--server", ["nginx", "apache"][srv.get_selected()]]
            self.run_verb(args, f"Creating {nm}…")
            if ssl.get_active():
                tld = self.last_data.get("config", {}).get("tld", "test")
                GLib.timeout_add_seconds(3, lambda: (self.run_verb(["secure", f"{nm}.{tld}"], None), False)[1])

        dlg.connect("response", resp)
        dlg.present()

    def _app_dialog(self, kind) -> None:
        title = "Add a Node app" if kind == "node" else "Add a Python app"
        dlg = Adw.MessageDialog(transient_for=self, heading=title,
                                body="A managed, supervised app served behind a *.test reverse proxy.")
        form = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        name = Gtk.Entry(placeholder_text="app name")
        folder = Gtk.Entry(placeholder_text="/path/to/project")
        cmd = Gtk.Entry(text="python app.py" if kind == "py" else "npm run dev")
        port = Gtk.SpinButton.new_with_range(1024, 65535, 1)
        port.set_value(8000 if kind == "py" else 3000)
        venv = Gtk.CheckButton(label="Create a virtualenv (.venv)", active=True)
        rows = [(name, "Name"), (folder, "Folder"), (cmd, "Command"), (port, "Port")]
        for w, lab in rows:
            row = Gtk.Box(spacing=10)
            row.append(Gtk.Label(label=lab, width_chars=10, xalign=0))
            w.set_hexpand(True)
            row.append(w)
            form.append(row)
        if kind == "py":
            form.append(venv)
        dlg.set_extra_child(form)
        dlg.add_response("cancel", "Cancel")
        dlg.add_response("ok", "Create")
        dlg.set_response_appearance("ok", Adw.ResponseAppearance.SUGGESTED)

        def resp(d, r):
            if r != "ok":
                return
            nm, fd = name.get_text().strip(), folder.get_text().strip()
            if not nm or not fd:
                self.toast("Name and folder are required")
                return
            p = str(int(port.get_value()))
            if kind == "py":
                args = ["pysite", "add", nm, "--dir", fd, "--port", p,
                        "--cmd", cmd.get_text(), "--venv", "yes" if venv.get_active() else "no"]
            else:
                args = ["nodesite", "add", nm, "--fe-dir", fd, "--fe-port", p, "--fe-cmd", cmd.get_text()]
            self.run_verb(args, f"Creating {nm}…")

        dlg.connect("response", resp)
        dlg.present()

    def create_db_dialog(self) -> None:
        dlg = Adw.MessageDialog(transient_for=self, heading="Create database", body="")
        form = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        name = Gtk.Entry(placeholder_text="database name")
        eng = Gtk.DropDown.new_from_strings(["mysql", "pg"])
        for w, lab in ((name, "Name"), (eng, "Engine")):
            row = Gtk.Box(spacing=10)
            row.append(Gtk.Label(label=lab, width_chars=10, xalign=0))
            w.set_hexpand(True)
            row.append(w)
            form.append(row)
        dlg.set_extra_child(form)
        dlg.add_response("cancel", "Cancel")
        dlg.add_response("ok", "Create")
        dlg.set_response_appearance("ok", Adw.ResponseAppearance.SUGGESTED)

        def resp(d, r):
            if r != "ok":
                return
            nm = name.get_text().strip()
            if not nm:
                return
            self.run_verb(["db", "create", nm, "--engine", ["mysql", "pg"][eng.get_selected()]], f"Creating {nm}…")

        dlg.connect("response", resp)
        dlg.present()

    # ── GUI prefs (separate from the engine config) ──
    def _gui(self) -> dict:
        try:
            return json.load(open(GUI_CFG))
        except Exception:
            return {}

    def cfg_int(self, key, default) -> int:
        return int(self._gui().get(key, default))

    def set_cfg(self, key, value) -> None:
        d = self._gui()
        d[key] = value
        os.makedirs(os.path.dirname(GUI_CFG), exist_ok=True)
        json.dump(d, open(GUI_CFG, "w"))


def _first_line(text: str) -> str:
    for ln in (text or "").splitlines():
        ln = ln.strip().lstrip("✗! ").strip()
        if ln:
            return ln[:120]
    return ""
