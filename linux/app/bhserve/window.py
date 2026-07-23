"""MainWindow — the Adw split-view shell: an 8-item sidebar, a content stack of the 8
panes, a toast overlay, and a 4-second `bhserve api` refresh loop. It also hosts the
helpers the pages call: run_verb (async verb + toast + refresh), confirm/choose dialogs,
the add-site / create-database dialogs, and the GUI-prefs store.
"""
from __future__ import annotations

import json
import os
import re
import threading

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, GLib, Gtk  # noqa: E402

from . import __version__  # noqa: E402
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
        self.app_version = __version__
        self.last_data: dict = {}
        self.pages: dict = {}
        self.applog: list = []   # recent verb activity, shown in the Dashboard activity log

        self.toast_overlay = Adw.ToastOverlay()
        split = Adw.OverlaySplitView()
        self.split = split
        self.toast_overlay.set_child(split)
        self.set_content(self.toast_overlay)

        # ── sidebar: app icon + name at the top, nav in the middle, Settings pinned
        #    at the bottom (parity with the Windows NavigationView / macOS source list) ──
        sidebar_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, css_classes=["bh-sidebar"])

        brand = Gtk.Box(spacing=10, margin_top=14, margin_bottom=14, margin_start=14, margin_end=12)
        app_icon = Gtk.Image.new_from_icon_name("com.biswashost.bhserve")
        app_icon.set_pixel_size(28)
        brand.append(app_icon)
        name_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, valign=Gtk.Align.CENTER, hexpand=True)
        name_box.append(Gtk.Label(label="BHServe", xalign=0, css_classes=["bh-brand"]))
        name_box.append(Gtk.Label(label="Local server", xalign=0, css_classes=["dim-label", "caption"]))
        brand.append(name_box)
        self.spinner = Gtk.Spinner(valign=Gtk.Align.CENTER)
        brand.append(self.spinner)
        sidebar_box.append(brand)
        sidebar_box.append(Gtk.Separator())

        # nav items (everything except Settings)
        self.sidebar_list = Gtk.ListBox(css_classes=["navigation-sidebar"])
        self.sidebar_list.connect("row-selected", self._on_nav)
        for key, label, icon, _cls in NAV:
            if key == "settings":
                continue
            self.sidebar_list.append(self._nav_row(key, label, icon))
        sb_scroll = Gtk.ScrolledWindow(vexpand=True)
        sb_scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        sb_scroll.set_child(self.sidebar_list)
        sidebar_box.append(sb_scroll)

        # Settings pinned to the bottom
        sidebar_box.append(Gtk.Separator())
        self.settings_list = Gtk.ListBox(css_classes=["navigation-sidebar"])
        self.settings_list.connect("row-selected", self._on_nav)
        self.settings_list.append(self._nav_row("settings", "Settings", "preferences-system-symbolic"))
        sidebar_box.append(self.settings_list)

        split.set_sidebar(sidebar_box)
        split.set_min_sidebar_width(220)
        split.set_max_sidebar_width(260)

        # ── content ──
        content_tv = Adw.ToolbarView()
        self.content_header = Adw.HeaderBar()
        self.sidebar_toggle = Gtk.ToggleButton(icon_name="sidebar-show-symbolic",
                                                tooltip_text="Toggle sidebar", active=True)
        self.sidebar_toggle.connect("toggled",
                                    lambda b: self.split.set_show_sidebar(b.get_active()))
        self.content_header.pack_start(self.sidebar_toggle)
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
        split.set_content(content_tv)
        # keep the toggle button in sync when the split collapses/expands on its own
        split.connect("notify::show-sidebar",
                      lambda s, _p: self.sidebar_toggle.set_active(s.get_show_sidebar()))

        self.sidebar_list.select_row(self.sidebar_list.get_row_at_index(0))
        self.refresh()
        GLib.timeout_add_seconds(4, self._tick)
        # auto update-check shortly after launch (throttled + gated by the toggle), then daily
        GLib.timeout_add_seconds(3, lambda: (self.check_updates(False), False)[1])
        GLib.timeout_add_seconds(24 * 3600, lambda: (self.check_updates(False), True)[1])

    # ── navigation ──
    def _nav_row(self, key: str, label: str, icon: str) -> Gtk.ListBoxRow:
        row = Gtk.ListBoxRow()
        b = Gtk.Box(spacing=12, margin_top=8, margin_bottom=8, margin_start=8, margin_end=8)
        b.append(Gtk.Image.new_from_icon_name(icon))
        b.append(Gtk.Label(label=label, xalign=0))
        row.set_child(b)
        row.nav_key = key
        return row

    def _on_nav(self, listbox, row) -> None:
        if not row:
            return
        # nav and Settings are two separate lists — clear the other so only one row
        # stays highlighted at a time.
        other = self.settings_list if listbox is self.sidebar_list else self.sidebar_list
        if other.get_selected_row() is not None:
            other.unselect_all()
        key = row.nav_key
        self.stack.set_visible_child_name(key)
        label = next(l for k, l, _i, _c in NAV if k == key)
        self.content_title.set_title(label)
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
    def run_verb(self, args, msg, refresh=True, then=None, env=None) -> None:
        if msg:
            self.toast(msg)
            self._applog(msg)
        self.spinner.start()

        def done(rc, out):
            self.spinner.stop()
            if rc != 0:
                err = _first_line(out) or f"{' '.join(args)} failed"
                self.toast(err)
                self._applog(f"✗ {err}")
            elif msg:
                self.toast(msg.replace("…", " — done"))
                self._applog(msg.replace("…", " — done"))
            if rc == 0 and then:   # chain a follow-up verb on success (e.g. install → use)
                self.run_verb(then[0], then[1], refresh=refresh)
            elif refresh:
                self.refresh()

        self.engine.run_async(list(args), done, env=env)

    def _applog(self, line: str) -> None:
        self.applog.append(line)
        del self.applog[:-200]

    def toast(self, text: str) -> None:
        self.toast_overlay.add_toast(Adw.Toast(title=text, timeout=3))

    # ── self-update ──
    def check_updates(self, force: bool = False) -> None:
        from . import updater
        if not force and not self.cfg_bool("auto_update", True):
            return

        def worker():
            rel = updater.check(force=force)
            if rel:
                GLib.idle_add(self._offer_update, rel)
            elif force:
                GLib.idle_add(self.toast, "You're on the latest version.")
        threading.Thread(target=worker, daemon=True).start()

    def _offer_update(self, rel: dict) -> bool:
        notes = (rel.get("notes") or "A new version is available.").strip()
        dlg = Adw.MessageDialog(transient_for=self,
                                heading=f"BHServe {rel['version']} is available",
                                body=notes[:400])
        dlg.add_response("later", "Later")
        dlg.add_response("install", "Install update")
        dlg.set_response_appearance("install", Adw.ResponseAppearance.SUGGESTED)
        dlg.connect("response", lambda d, r: self._do_update(rel) if r == "install" else None)
        dlg.present()
        return False

    def about(self) -> None:
        win = Adw.AboutWindow(
            transient_for=self,
            application_name="BHServe",
            application_icon="com.biswashost.bhserve",
            version=self.app_version,
            developer_name="BiswasHost",
            website="https://www.biswashost.com",
            comments="A free, self-controlled local web server for Linux —\na clean alternative to XAMPP.",
            license_type=Gtk.License.MIT_X11,
        )
        win.present()

    def _do_update(self, rel: dict) -> None:
        from . import updater
        self.spinner.start()

        def worker():
            ok, msg = updater.download_and_install(
                rel["deb_url"], lambda s: GLib.idle_add(self.toast, s))
            GLib.idle_add(self.spinner.stop)
            GLib.idle_add(self.toast, msg)
        threading.Thread(target=worker, daemon=True).start()

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
        form.set_margin_top(12)
        form.set_margin_bottom(12)
        form.set_margin_start(12)
        form.set_margin_end(12)
        name = Gtk.Entry(placeholder_text="site name (e.g. myshop)")
        typ = Gtk.DropDown.new_from_strings(["wordpress", "php", "laravel", "others"])
        # Offer only the PHP versions actually installed (so you can't pick one that isn't there);
        # fall back to the full list if none installed yet.
        installed_php = [s["key"].replace("php@", "") for s in self.last_data.get("services", [])
                         if s["role"] == "php" and s["installed"]]
        php_choices = installed_php or [k.replace("php@", "") for k in P.PHP_KEYS]
        php = Gtk.DropDown.new_from_strings(php_choices)
        # Labels are descriptive; the actual --server value is mapped by index below (nginx / apache / ols).
        srv = Gtk.DropDown.new_from_strings(["nginx — serves PHP",
                                             "Apache — + nginx, for .htaccess",
                                             "OpenLiteSpeed — + nginx, .htaccess + LSCache"])
        srv.set_tooltip_text("nginx serves PHP on its own — all you need for PHP/WordPress. "
                             "Apache and OpenLiteSpeed are for sites needing native .htaccess; both run "
                             "behind nginx, so choosing them uses nginx too. OpenLiteSpeed auto-reloads "
                             "on .htaccess changes and supports the LiteSpeed Cache plugin "
                             "(installed automatically on first use).")
        ssl = Gtk.CheckButton(label="Enable trusted HTTPS (mkcert)", active=True)
        dir_entry = Gtk.Entry(placeholder_text="Default folder (optional)", hexpand=True)
        dir_box = Gtk.Box(spacing=6)
        dir_box.append(dir_entry)
        browse_btn = Gtk.Button(label="Browse…", valign=Gtk.Align.CENTER)
        browse_btn.set_tooltip_text("Select project directory")
        dir_box.append(browse_btn)

        for w, lab in ((name, "Name"), (typ, "Type"), (php, "PHP"), (srv, "Web server"), (dir_box, "Location")):
            row = Gtk.Box(spacing=10)
            row.append(Gtk.Label(label=lab, width_chars=10, xalign=0))
            w.set_hexpand(True)
            row.append(w)
            form.append(row)

        def _pick_dir(btn, entry):
            def on_pick(dialog, result):
                try:
                    f = dialog.select_folder_finish(result)
                    if f:
                        entry.set_text(f.get_path())
                except Exception:
                    pass

            dlg = Gtk.FileDialog()
            dlg.set_title("Select project directory")
            dlg.select_folder(self, None, on_pick)

        browse_btn.connect("clicked", _pick_dir, dir_entry)
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
                    "--type", ["wordpress", "php", "laravel", "others"][typ.get_selected()],
                    "--php", php_choices[php.get_selected()],
                    "--server", ["nginx", "apache", "ols"][srv.get_selected()]]
            d = dir_entry.get_text().strip()
            if d:
                args += ["--root", d]
            tld = self.last_data.get("config", {}).get("tld", "test")
            self._run_add_site(nm, args, ssl.get_active(), tld)

        dlg.connect("response", resp)
        dlg.present()

    # Run `site add` (+ optional `secure`) and show a proper result dialog when the whole
    # flow finishes — parity with the Windows/macOS "Site created" banner (was only a toast).
    def _run_add_site(self, nm, args, do_secure, tld) -> None:
        self.toast(f"Creating {nm}…")
        self._applog(f"Creating {nm}…")
        self.spinner.start()

        def finish(ok, output):
            self.spinner.stop()
            self.refresh()
            self._applog(f"{'✓' if ok else '✗'} {nm} " + ("created" if ok else "failed"))
            self._site_result_dialog(ok, output, nm, tld)

        def after_add(rc, out):
            if rc != 0:
                finish(False, out)
            elif do_secure:
                self.engine.run_async(["secure", f"{nm}.{tld}"],
                                      lambda rc2, out2: finish(True, out + "\n" + out2))
            else:
                finish(True, out)

        self.engine.run_async(list(args), after_add)

    def _site_result_dialog(self, ok, output, nm, tld) -> None:
        m = re.search(r"https://\S+", output) or re.search(r"https?://\S+", output)
        url = (m.group(0).rstrip(".") if m else f"http://{nm}.{tld}")
        dlg = Adw.MessageDialog(
            transient_for=self,
            heading=("Site created" if ok else "Couldn’t create site"),
            body=(f"{nm}.{tld} is ready." if ok else f"Something went wrong creating {nm}."))
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        box.set_size_request(480, -1)   # widen the dialog so long paths read on one/two lines
        if ok:
            box.append(Gtk.Label(label=url, xalign=0, selectable=True,
                                  css_classes=["bh-brand"], wrap=True))
        # step lines (✓ / ✗ / ! from the engine output), color-coded
        steps = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4, css_classes=["card"],
                        margin_top=6, margin_bottom=6, margin_start=10, margin_end=10)
        shown = 0
        cls = {"✓": "bh-step-ok", "✗": "bh-step-err", "!": "bh-step-warn"}
        for raw in output.replace("\r", "").split("\n"):
            t = raw.strip()
            if not t or t[0] not in cls:
                continue
            steps.append(Gtk.Label(label=t, xalign=0, wrap=True, css_classes=[cls[t[0]]]))
            shown += 1
            if shown >= 14:
                break
        if shown:
            sc = Gtk.ScrolledWindow(max_content_height=260, propagate_natural_height=True)
            sc.set_min_content_width(460)
            sc.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
            sc.set_child(steps)
            box.append(sc)
        dlg.set_extra_child(box)
        dlg.add_response("close", "Close")
        if ok:
            dlg.add_response("open", "Open site")
            dlg.set_response_appearance("open", Adw.ResponseAppearance.SUGGESTED)
        dlg.connect("response", lambda d, r: P._open(url) if r == "open" else None)
        dlg.present()

    def _app_dialog(self, kind) -> None:
        title = "Add a Node app" if kind == "node" else "Add a Python app"
        dlg = Adw.MessageDialog(transient_for=self, heading=title,
                                body="A managed, supervised app served behind a *.test reverse proxy.")
        form = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        folder = Gtk.Entry(placeholder_text="/path/to/project", hexpand=True)
        folder_box = Gtk.Box(spacing=8)
        folder_box.append(folder)
        browse_btn = Gtk.Button(label="Browse…", valign=Gtk.Align.CENTER)

        def _pick_folder(btn, entry):
            def on_pick(dialog, result):
                try:
                    f = dialog.select_folder_finish(result)
                    if f:
                        entry.set_text(f.get_path())
                except Exception:
                    pass

            dlg = Gtk.FileDialog()
            dlg.set_title("Select project directory")
            dlg.select_folder(self, None, on_pick)

        browse_btn.connect("clicked", _pick_folder, folder)
        folder_box.append(browse_btn)

        cmd = Gtk.Entry(text="python app.py" if kind == "py" else "npm run dev")
        port = Gtk.SpinButton.new_with_range(1024, 65535, 1)
        port.set_value(8000 if kind == "py" else 3000)
        venv = Gtk.CheckButton(label="Create a virtualenv (.venv)", active=True)
        rows = [(name, "Name"), (folder_box, "Folder"), (cmd, "Command"), (port, "Port")]
        for w, lab in rows:
            row = Gtk.Box(spacing=10)
            row.append(Gtk.Label(label=lab, width_chars=10, xalign=0))
            if w is not folder_box:
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
        pw = Gtk.Entry(placeholder_text="user password (MySQL, optional)", hexpand=True)
        pwrow = Gtk.Box(spacing=8)
        pwrow.append(pw)
        gen = Gtk.Button(label="Generate", valign=Gtk.Align.CENTER)
        gen.connect("clicked", lambda *_: pw.set_text(self._gen_password()))
        pwrow.append(gen)
        for w, lab in ((name, "Name"), (eng, "Engine")):
            row = Gtk.Box(spacing=10)
            row.append(Gtk.Label(label=lab, width_chars=10, xalign=0))
            w.set_hexpand(True)
            row.append(w)
            form.append(row)
        prow = Gtk.Box(spacing=10)
        prow.append(Gtk.Label(label="Password", width_chars=10, xalign=0))
        prow.append(pwrow)
        form.append(prow)
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
            args = ["db", "create", nm, "--engine", ["mysql", "pg"][eng.get_selected()]]
            env = {"BHSERVE_DB_PASSWORD": pw.get_text()} if pw.get_text() else None
            self.run_verb(args, f"Creating {nm}…", env=env)  # password via env, not argv/ps

        dlg.connect("response", resp)
        dlg.present()

    # ── database management (parity with the Windows Databases page) ──
    @staticmethod
    def _gen_password(n: int = 16) -> str:
        import secrets
        alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789"
        return "".join(secrets.choice(alphabet) for _ in range(n))

    def _pw_dialog(self, heading, body, hint, on_apply, apply_label="Apply", initial=""):
        dlg = Adw.MessageDialog(transient_for=self, heading=heading, body=body)
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=8)
        entry = Gtk.Entry(placeholder_text=hint, text=initial, hexpand=True)
        rowb = Gtk.Box(spacing=8)
        entry.set_hexpand(True)
        rowb.append(entry)
        gen = Gtk.Button(label="Generate", valign=Gtk.Align.CENTER)
        gen.connect("clicked", lambda *_: entry.set_text(self._gen_password()))
        rowb.append(gen)
        box.append(rowb)
        dlg.set_extra_child(box)
        dlg.add_response("cancel", "Cancel")
        dlg.add_response("ok", apply_label)
        dlg.set_response_appearance("ok", Adw.ResponseAppearance.SUGGESTED)
        dlg.connect("response", lambda d, r: on_apply(entry.get_text()) if r == "ok" else None)
        dlg.present()

    def db_root_dialog(self) -> None:
        # Pass the password via BHSERVE_DB_PASSWORD env, not argv — keeps it out of `ps`.
        self._pw_dialog(
            "Root password",
            "Sets the MySQL/MariaDB root password BHServe uses everywhere (new WordPress sites + "
            "phpMyAdmin). Leave blank to remove it. Local-dev only.",
            "new root password (blank = remove)",
            lambda pw: self.run_verb(["db", "root-passwd"],
                                     "Setting root password…" if pw else "Removing root password…",
                                     env={"BHSERVE_DB_PASSWORD": pw}),
            apply_label="Apply")

    def db_password_dialog(self, name) -> None:
        self._pw_dialog(
            f"Set password · {name}",
            f"Creates/updates a dedicated user “{name}” (@localhost + @127.0.0.1) for this database.",
            "new password",
            lambda pw: self.run_verb(["db", "passwd", name, "--engine", "mysql"],
                                     f"Setting password for {name}…",
                                     env={"BHSERVE_DB_PASSWORD": pw}) if pw else None,
            apply_label="Set")

    def db_drop(self, name, engine="mysql") -> None:
        self.confirm(
            f"Drop database “{name}”?",
            f"Permanently drops '{name}' ({'PostgreSQL' if engine == 'pg' else 'MySQL/MariaDB'}). "
            "This cannot be undone.",
            lambda: self.run_verb(["db", "drop", name, "--engine", engine], f"Dropping {name}…"))

    # ── Cloudflare quick tunnel: "Share publicly" (parity with Windows/macOS) ──
    def _copy(self, text) -> None:
        try:
            self.get_clipboard().set(text)
            self.toast("Link copied")
        except Exception:  # noqa: BLE001
            pass

    def site_share(self, name) -> None:
        site = next((x for x in self.last_data.get("sites", []) if x.get("name") == name), None)
        url = (site or {}).get("tunnel", "")
        if url:                       # already sharing → just show the manage sheet
            self._share_dialog(name, url)
            return
        self.toast(f"Starting public share for {name}…")
        self._applog(f"Sharing {name} via Cloudflare…")
        self.spinner.start()

        def done(rc, out):
            self.spinner.stop()
            self.refresh()
            m = re.search(r"https://[a-z0-9-]+\.trycloudflare\.com", out)
            if m:
                self._share_dialog(name, m.group(0))
            else:
                err = Adw.MessageDialog(
                    transient_for=self, heading="Couldn’t share publicly",
                    body=(_first_line(out) or "The tunnel didn’t return a public URL. "
                          "Check Logs and try again."))
                err.add_response("close", "Close")
                err.present()

        # First share auto-downloads cloudflared — can take a few extra seconds.
        self.engine.run_async(["tunnel", "start", name], done)

    def _share_dialog(self, name, url) -> None:
        dlg = Adw.MessageDialog(
            transient_for=self, heading=f"Share “{name}” publicly",
            body="Cloudflare Tunnel gives this site a temporary public https address — no account "
                 "or port-forwarding. The link works while sharing is on.")
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=12)
        box.set_size_request(460, -1)
        live = Gtk.Box(spacing=8)
        live.append(Gtk.Label(label="●", css_classes=["dot-on"]))
        live.append(Gtk.Label(label="Live — anyone with this link can reach your site.",
                              xalign=0, css_classes=["bh-step-ok"]))
        box.append(live)
        row = Gtk.Box(spacing=6)
        entry = Gtk.Entry(text=url, hexpand=True)
        entry.set_editable(False)
        row.append(entry)
        cp = Gtk.Button(icon_name="edit-copy-symbolic", tooltip_text="Copy link", valign=Gtk.Align.CENTER)
        cp.connect("clicked", lambda *_: self._copy(url))
        row.append(cp)
        ob = Gtk.Button(icon_name="web-browser-symbolic", tooltip_text="Open in browser", valign=Gtk.Align.CENTER)
        ob.connect("clicked", lambda *_: P._open(url))
        row.append(ob)
        box.append(row)
        dlg.set_extra_child(box)
        dlg.add_response("close", "Close")
        dlg.add_response("stop", "Stop sharing")
        dlg.set_response_appearance("stop", Adw.ResponseAppearance.DESTRUCTIVE)
        dlg.connect("response", lambda d, r: self.run_verb(["tunnel", "stop", name],
                    f"Stopped sharing {name}") if r == "stop" else None)
        dlg.present()

    # ── GUI prefs (separate from the engine config) ──
    def _gui(self) -> dict:
        try:
            return json.load(open(GUI_CFG))
        except Exception:
            return {}

    def cfg_int(self, key, default) -> int:
        return int(self._gui().get(key, default))

    def cfg_bool(self, key, default) -> bool:
        return bool(self._gui().get(key, default))

    def set_cfg(self, key, value) -> None:
        d = self._gui()
        d[key] = value
        os.makedirs(os.path.dirname(GUI_CFG), exist_ok=True)
        json.dump(d, open(GUI_CFG, "w"))


def _first_line(text: str) -> str:
    lines = [ln.strip().lstrip("✗!✓ ").strip() for ln in (text or "").splitlines() if ln.strip()]
    # Prefer the actual error (apt 'E:'/'Err:' or a '… failed' line) over a header line.
    for ln in lines:
        low = ln.lower()
        if ln.startswith(("E:", "Err")) or "failed" in low or "could not" in low or "unable to" in low:
            return ln[:180]
    return (lines[0] if lines else "")[:180]
