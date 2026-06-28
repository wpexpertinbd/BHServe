"""The 8 panes (parity with macOS v1.7.4): Dashboard / Services / Sites / Databases /
Node / Python / Logs / Settings. Each Page is a Gtk.Box with a refresh(api) method the
window calls after every `bhserve api` snapshot.
"""
from __future__ import annotations

import os
import re
import shlex
import shutil
import subprocess
from typing import Callable

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, Gio, GLib, Gtk  # noqa: E402

from .widgets import PAGE_SIZES, PagedList, page_size_to_int, pill, status_dot  # noqa: E402

PHP_KEYS = ["php@8.4", "php@8.3", "php@8.2", "php@8.1", "php@7.4"]
SERVICE_GROUPS = [
    ("PHP", "php"),
    ("Web servers", "web"),
    ("Databases", "db"),
    ("Cache", "cache"),
    ("DNS / TLS / Mail", "dns tls mail"),
    ("Runtimes", "node python"),
]
ROLE_LABEL = {
    "php": "PHP", "web": "Web", "db": "Database", "cache": "Cache",
    "dns": "DNS", "tls": "TLS", "mail": "Mail", "node": "Node", "python": "Python",
}


def clean_version(s: str) -> str:
    """The engine's version probe can truncate ('PHP 8.4.22 (fpm-fcgi) (built: …'); keep
    just the meaningful 'Name X.Y.Z'."""
    if not s:
        return ""
    m = re.search(r"(\d+\.\d+(?:\.\d+)?)", s)
    return m.group(1) if m else s.strip()[:24]


def _open(path_or_url: str) -> None:
    try:
        Gio.AppInfo.launch_default_for_uri(
            path_or_url if "://" in path_or_url else GLib.filename_to_uri(path_or_url, None),
            None,
        )
    except Exception:
        subprocess.Popen(["xdg-open", path_or_url])


def _open_editor(folder: str) -> None:
    for ed in ("code", "codium", "cursor", "subl", "gnome-text-editor", "gedit"):
        if shutil.which(ed):
            subprocess.Popen([ed, folder])
            return
    _open(folder)


def _open_terminal(folder: str) -> None:
    for term, args in (
        ("gnome-terminal", ["--working-directory", folder]),
        ("konsole", ["--workdir", folder]),
        ("xfce4-terminal", ["--working-directory", folder]),
        ("xterm", ["-e", "bash", "-c", f"cd {shlex.quote(folder)} && exec bash"]),
    ):
        if shutil.which(term):
            subprocess.Popen([term, *args])
            return


# ─────────────────────────────────────────────────────────────────────────────
class DashboardPage(Gtk.Box):
    def __init__(self, win) -> None:
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=16,
                         margin_top=18, margin_bottom=18, margin_start=18, margin_end=18)
        self.win = win

        self.hero = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4, css_classes=["bh-hero"])
        self.hero_title = Gtk.Label(label="BHServe", xalign=0, css_classes=["title"])
        self.hero_sub = Gtk.Label(label="", xalign=0)
        self.hero.append(self.hero_title)
        self.hero.append(self.hero_sub)
        self.append(self.hero)

        self.metrics = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=12, homogeneous=True)
        self.m_sites = self._metric("Websites", "0")
        self.m_running = self._metric("Services running", "0")
        self.m_php = self._metric("PHP versions", "0")
        self.m_mem = self._metric("Memory used", "—")
        self.m_disk = self._metric("Disk used", "—")
        for m in (self.m_sites, self.m_running, self.m_php, self.m_mem, self.m_disk):
            self.metrics.append(m["card"])
        self.append(self.metrics)

        self.append(Gtk.Label(label="Services", xalign=0, css_classes=["heading"]))
        self.svc_flow = Gtk.FlowBox(selection_mode=Gtk.SelectionMode.NONE, max_children_per_line=4,
                                    column_spacing=8, row_spacing=8, homogeneous=True)
        self.append(self.svc_flow)

    def _metric(self, cap, val):
        card = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2, css_classes=["card", "bh-metric"])
        v = Gtk.Label(label=val, xalign=0, css_classes=["bh-metric-val"])
        c = Gtk.Label(label=cap, xalign=0, css_classes=["bh-metric-cap", "dim-label"])
        card.append(v)
        card.append(c)
        return {"card": card, "val": v}

    def refresh(self, data: dict) -> None:
        cfg = data.get("config", {})
        services = data.get("services", [])
        sites = data.get("sites", [])
        self.hero_title.set_label(f"BHServe · {os.uname().nodename}")
        php_installed = [s for s in services if s["role"] == "php" and s["installed"]]
        running = [s for s in services if s.get("running")]
        self.hero_sub.set_label(
            f"{len(sites)} site(s) · default PHP {cfg.get('default_php','?')} · "
            f"web {cfg.get('default_web','nginx')} · *.{cfg.get('tld','test')}"
        )
        self.m_sites["val"].set_label(str(len(sites)))
        self.m_running["val"].set_label(str(len(running)))
        self.m_php["val"].set_label(str(len(php_installed)))
        mem, disk = _mem_disk()
        self.m_mem["val"].set_label(mem)
        self.m_disk["val"].set_label(disk)

        child = self.svc_flow.get_first_child()
        while child:
            nxt = child.get_next_sibling()
            self.svc_flow.remove(child)
            child = nxt
        for s in services:
            if not s["installed"]:
                continue
            box = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8, css_classes=["card", "bh-metric"])
            box.append(status_dot(s.get("running", False)))
            lab = Gtk.Label(label=s["key"], xalign=0, hexpand=True)
            box.append(lab)
            self.svc_flow.append(box)


def _mem_disk():
    mem = disk = "—"
    try:
        info = {}
        for line in open("/proc/meminfo"):
            k, _, v = line.partition(":")
            info[k] = int(v.strip().split()[0])
        total, avail = info.get("MemTotal", 0), info.get("MemAvailable", 0)
        if total:
            mem = f"{round(100 * (total - avail) / total)}%"
    except Exception:
        pass
    try:
        st = os.statvfs(os.path.expanduser("~"))
        used = (st.f_blocks - st.f_bfree) / st.f_blocks
        disk = f"{round(100 * used)}%"
    except Exception:
        pass
    return mem, disk


# ─────────────────────────────────────────────────────────────────────────────
class ServicesPage(Gtk.Box):
    def __init__(self, win) -> None:
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=12,
                         margin_top=18, margin_bottom=18, margin_start=18, margin_end=18)
        self.win = win
        self.scroller = Gtk.ScrolledWindow(vexpand=True)
        self.body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=18)
        self.scroller.set_child(self.body)
        self.append(self.scroller)

    def refresh(self, data: dict) -> None:
        services = data.get("services", [])
        child = self.body.get_first_child()
        while child:
            nxt = child.get_next_sibling()
            self.body.remove(child)
            child = nxt
        for title, roles in SERVICE_GROUPS:
            group_svcs = [s for s in services if s["role"] in roles.split()]
            if not group_svcs:
                continue
            grp = Adw.PreferencesGroup(title=title)
            for s in group_svcs:
                grp.add(self._row(s))
            self.body.append(grp)

    def _row(self, s: dict) -> Adw.ActionRow:
        installed, running = s["installed"], s.get("running")
        sub = clean_version(s.get("version", "")) or s.get("formula", "")
        row = Adw.ActionRow(title=s["key"], subtitle=sub)
        row.add_prefix(status_dot(running))

        box = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6, valign=Gtk.Align.CENTER)
        key = s["key"]
        if not installed:
            b = Gtk.Button(label="Install", css_classes=["suggested-action"])
            b.connect("clicked", lambda *_: self.win.run_verb(["install", key], f"Installing {key}…"))
            box.append(b)
        else:
            if s["role"] in ("php", "web", "db", "cache", "mail"):
                if running:
                    b = Gtk.Button(icon_name="media-playback-stop-symbolic", tooltip_text="Stop")
                    b.connect("clicked", lambda *_: self.win.run_verb(["stop", key], f"Stopping {key}…"))
                else:
                    b = Gtk.Button(icon_name="media-playback-start-symbolic", tooltip_text="Start")
                    b.connect("clicked", lambda *_: self.win.run_verb(["start", key], f"Starting {key}…"))
                box.append(b)
            star = Gtk.ToggleButton(icon_name="starred-symbolic", tooltip_text="Auto-start", active=s.get("enabled", False))
            star.connect("toggled", lambda btn: self.win.run_verb(
                ["enable" if btn.get_active() else "disable", key], None, refresh=True))
            box.append(star)
            upd = Gtk.Button(icon_name="software-update-available-symbolic", tooltip_text="Update to latest")
            upd.connect("clicked", lambda *_: self.win.run_verb(["update", key], f"Updating {key}…"))
            box.append(upd)
            if s["role"] == "php":
                ini = Gtk.Button(icon_name="document-edit-symbolic", tooltip_text="Edit php.ini")
                ini.connect("clicked", lambda *_: self._edit_ini(key))
                box.append(ini)
            rm = Gtk.Button(icon_name="user-trash-symbolic", tooltip_text="Uninstall", css_classes=["destructive-action"])
            rm.connect("clicked", lambda *_: self.win.confirm(
                f"Uninstall {key}?", "The service binary is removed; your data and configs stay.",
                lambda: self.win.run_verb(["uninstall", key], f"Uninstalling {key}…")))
            box.append(rm)
        row.add_suffix(box)
        return row

    def _edit_ini(self, key: str) -> None:
        rc, out = self.win.engine.run("php", "ini", "path", key.replace("php@", ""))
        path = out.strip().splitlines()[-1].strip() if out.strip() else ""
        if path and os.path.exists(path):
            _open_editor(os.path.dirname(path)) if not shutil.which("gnome-text-editor") else subprocess.Popen(["gnome-text-editor", path])
        else:
            self.win.toast("Couldn't resolve php.ini path")


# ─────────────────────────────────────────────────────────────────────────────
class SitesPage(Gtk.Box):
    def __init__(self, win) -> None:
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=12,
                         margin_top=18, margin_bottom=18, margin_start=18, margin_end=18)
        self.win = win
        header = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        header.append(Gtk.Label(label="Websites", xalign=0, hexpand=True, css_classes=["title-2"]))
        add = Gtk.Button(label="Add site", icon_name="list-add-symbolic", css_classes=["suggested-action"])
        add.connect("clicked", lambda *_: self._add_dialog())
        header.append(add)
        self.append(header)
        self.list = PagedList(self._row, self._match,
                              page_size=self.win.cfg_int("sites_page_size", 15),
                              empty_text="No sites yet — click “Add site”.",
                              on_page_size_changed=lambda n: self.win.set_cfg("sites_page_size", n))
        self.append(self.list)

    def _match(self, s, q):
        return q.lower() in s["name"].lower() or q.lower() in s.get("php", "").lower()

    def refresh(self, data: dict) -> None:
        self.list.set_items(data.get("sites", []))

    def _row(self, s: dict) -> Adw.ActionRow:
        scheme = "https" if s.get("secure") else "http"
        row = Adw.ActionRow(title=s["domain"],
                            subtitle=f"{s.get('php','')} · {s.get('server','nginx')} · {scheme}")
        row.add_prefix(status_dot(s.get("enabled", True)))
        box = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6, valign=Gtk.Align.CENTER)
        if s.get("secure"):
            box.append(pill("HTTPS", "bh-pill-blue"))
        openb = Gtk.Button(icon_name="web-browser-symbolic", tooltip_text="Open in browser")
        openb.connect("clicked", lambda *_: _open(f"{scheme}://{s['domain']}"))
        box.append(openb)
        menu = Gtk.MenuButton(icon_name="view-more-symbolic", tooltip_text="More")
        menu.set_popover(self._row_menu(s))
        box.append(menu)
        row.add_suffix(box)
        return row

    def _row_menu(self, s: dict) -> Gtk.Popover:
        pop = Gtk.Popover()
        v = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=2, margin_top=6, margin_bottom=6,
                    margin_start=6, margin_end=6)

        def item(label, icon, cb, destructive=False):
            b = Gtk.Button(css_classes=["flat"] + (["destructive-action"] if destructive else []))
            inner = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
            inner.append(Gtk.Image.new_from_icon_name(icon))
            inner.append(Gtk.Label(label=label, xalign=0, hexpand=True))
            b.set_child(inner)
            b.connect("clicked", lambda *_: (pop.popdown(), cb()))
            v.append(b)

        name = s["name"]
        item("Change PHP version…", "application-x-php-symbolic", lambda: self._change_php(s))
        item("Switch web server…", "network-server-symbolic", lambda: self._change_server(s))
        if not s.get("secure"):
            item("Enable HTTPS", "security-high-symbolic",
                 lambda: self.win.run_verb(["secure", s["domain"]], f"Securing {s['domain']}…"))
        item("Open folder", "folder-symbolic", lambda: _open(s["root"]))
        item("Open in editor", "text-editor-symbolic", lambda: _open_editor(s["root"]))
        item("Open terminal", "utilities-terminal-symbolic", lambda: _open_terminal(s["root"]))
        item("Delete site…", "user-trash-symbolic", lambda: self.win.confirm(
            f"Delete site “{name}”?", "Removes the vhost. Tick purge in the next step to also drop files + DB.",
            lambda: self.win.run_verb(["site", "rm", name], f"Removing {name}…")), destructive=True)
        pop.set_child(v)
        return pop

    def _change_php(self, s):
        installed = [x["key"] for x in self.win.last_data.get("services", [])
                     if x["role"] == "php" and x["installed"]]
        installed = [k.replace("php@", "") for k in installed]
        self.win.choose("Change PHP version", f"Pick a PHP version for {s['name']}", installed,
                        lambda v: self.win.run_verb(["site", "php", s["name"], v], f"Switching {s['name']} → PHP {v}…"))

    def _change_server(self, s):
        self.win.choose("Switch web server", f"Serve {s['name']} via:", ["nginx", "apache"],
                        lambda v: self.win.run_verb(["site", "server", s["name"], v], f"Switching {s['name']} → {v}…"))

    def _add_dialog(self):
        self.win.add_site_dialog()


# ─────────────────────────────────────────────────────────────────────────────
class _AppsPage(Gtk.Box):
    """Shared base for Node + Python panes (managed runtime + an apps PagedList)."""
    KIND = "node"
    TITLE = "Node apps"

    def __init__(self, win) -> None:
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=12,
                         margin_top=18, margin_bottom=18, margin_start=18, margin_end=18)
        self.win = win
        self.runtime = Adw.PreferencesGroup(title="Runtime")
        self.rt_row = Adw.ActionRow(title="…")
        self.runtime.add(self.rt_row)
        self.append(self.runtime)
        header = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        header.append(Gtk.Label(label=self.TITLE, xalign=0, hexpand=True, css_classes=["title-4"]))
        add = Gtk.Button(label="Add app", icon_name="list-add-symbolic", css_classes=["suggested-action"])
        add.connect("clicked", lambda *_: self.win.add_site_dialog(default_type=self.KIND))
        header.append(add)
        self.append(header)
        self.list = PagedList(self._row, lambda a, q: q.lower() in a.get("name", "").lower(),
                              page_size=self.win.cfg_int("apps_page_size", 15),
                              empty_text=f"No {self.KIND} apps yet.",
                              on_page_size_changed=lambda n: self.win.set_cfg("apps_page_size", n))
        self.append(self.list)

    def _apps(self):
        rc, out = self.win.engine.run(f"{self.KIND}site", "list")
        apps = []
        for line in out.splitlines():
            line = line.strip()
            m = re.search(r"([a-z0-9][a-z0-9._-]*)", line, re.I)
            if m and ".test" not in line and "usage" not in line.lower() and len(line) > 1:
                if m.group(1) not in ("python", "node", "site", "app"):
                    apps.append({"name": m.group(1), "line": line})
        return apps

    def _row(self, a: dict) -> Adw.ActionRow:
        name = a["name"]
        row = Adw.ActionRow(title=name, subtitle=a.get("line", ""))
        box = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6, valign=Gtk.Align.CENTER)
        for icon, tip, verb in (("media-playback-start-symbolic", "Start", "start"),
                                 ("media-playback-stop-symbolic", "Stop", "stop"),
                                 ("view-refresh-symbolic", "Restart", "restart")):
            b = Gtk.Button(icon_name=icon, tooltip_text=tip)
            b.connect("clicked", lambda _w, v=verb: self.win.run_verb([f"{self.KIND}site", v, name], f"{v} {name}…"))
            box.append(b)
        rm = Gtk.Button(icon_name="user-trash-symbolic", tooltip_text="Remove", css_classes=["destructive-action"])
        rm.connect("clicked", lambda *_: self.win.run_verb([f"{self.KIND}site", "rm", name], f"Removing {name}…"))
        box.append(rm)
        row.add_suffix(box)
        return row

    def refresh(self, data: dict) -> None:
        self.list.set_items(self._apps())


class NodePage(_AppsPage):
    KIND, TITLE = "node", "Node apps"

    def refresh(self, data):
        rc, out = self.win.engine.run("node", "list")
        cur = next((l.strip() for l in out.splitlines() if l.strip()), "no versions")
        installed = any(s["key"] == "fnm" and s["installed"] for s in data.get("services", []))
        self.rt_row.set_title("Node (fnm)" if installed else "Node (fnm) — not installed")
        self.rt_row.set_subtitle(out.strip()[:80] if installed else "Install fnm from Services to manage Node versions")
        super().refresh(data)


class PythonPage(_AppsPage):
    KIND, TITLE = "py", "Python apps"

    def __init__(self, win):
        super().__init__(win)

    def refresh(self, data):
        py = next((s for s in data.get("services", []) if s["key"] == "python"), {})
        self.rt_row.set_title("Python" + (f" {clean_version(py.get('version',''))}" if py.get("installed") else " — not installed"))
        self.rt_row.set_subtitle("Ready for venv-backed apps" if py.get("installed") else "Install from Services")
        super().refresh(data)


# ─────────────────────────────────────────────────────────────────────────────
class DatabasesPage(Gtk.Box):
    def __init__(self, win) -> None:
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=12,
                         margin_top=18, margin_bottom=18, margin_start=18, margin_end=18)
        self.win = win
        self.servers = Adw.PreferencesGroup(title="Database servers")
        self.append(self.servers)
        header = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        header.append(Gtk.Label(label="Databases", xalign=0, hexpand=True, css_classes=["title-4"]))
        add = Gtk.Button(label="Create database", icon_name="list-add-symbolic", css_classes=["suggested-action"])
        add.connect("clicked", lambda *_: self.win.create_db_dialog())
        header.append(add)
        self.append(header)
        self.list = PagedList(lambda n: Adw.ActionRow(title=n),
                              lambda n, q: q.lower() in n.lower(),
                              page_size=self.win.cfg_int("databases_page_size", 15),
                              empty_text="No databases yet.",
                              on_page_size_changed=lambda n: self.win.set_cfg("databases_page_size", n))
        self.append(self.list)

    def refresh(self, data: dict) -> None:
        child = self.servers.get_first_child()
        # PreferencesGroup: clear by tracking rows
        for s in [x for x in data.get("services", []) if x["role"] == "db"]:
            pass
        self._render_servers(data)
        self._render_dbs(data)

    def _render_servers(self, data):
        # rebuild the group
        new = Adw.PreferencesGroup(title="Database servers")
        for s in [x for x in data.get("services", []) if x["role"] == "db"]:
            row = Adw.ActionRow(title=s["key"], subtitle=clean_version(s.get("version", "")) or s["formula"])
            row.add_prefix(status_dot(s.get("running")))
            box = Gtk.Box(spacing=6, valign=Gtk.Align.CENTER)
            key = s["key"]
            if not s["installed"]:
                b = Gtk.Button(label="Install", css_classes=["suggested-action"])
                b.connect("clicked", lambda _w, k=key: self.win.run_verb(["install", k], f"Installing {k}…"))
                box.append(b)
            else:
                verb = "stop" if s.get("running") else "start"
                icon = "media-playback-stop-symbolic" if s.get("running") else "media-playback-start-symbolic"
                b = Gtk.Button(icon_name=icon)
                b.connect("clicked", lambda _w, k=key, v=verb: self.win.run_verb([v, k], f"{v} {k}…"))
                box.append(b)
            row.add_suffix(box)
            new.add(row)
        self.servers.get_parent().insert_child_after(new, self.servers) if False else None
        # replace old group widget in place
        parent = self.servers.get_parent()
        if parent:
            parent.remove(self.servers)
            parent.insert_child_after(new, None)
        self.servers = new

    def _render_dbs(self, data):
        rc, out = self.win.engine.run("db", "list")
        dbs = [l.strip().lstrip("✓✗ ").split()[0] for l in out.splitlines()
               if l.strip() and not l.strip().startswith(("BHServe", "[", "data"))]
        dbs = [d for d in dbs if re.match(r"^[A-Za-z0-9_]+$", d)]
        self.list.set_items(dbs)


# ─────────────────────────────────────────────────────────────────────────────
class LogsPage(Gtk.Box):
    def __init__(self, win) -> None:
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=10,
                         margin_top=18, margin_bottom=18, margin_start=18, margin_end=18)
        self.win = win
        top = Gtk.Box(spacing=8)
        self.dd = Gtk.DropDown.new_from_strings(["(refresh)"])
        self.dd.connect("notify::selected", lambda *_: self._load())
        top.append(Gtk.Label(label="Log", css_classes=["dim-label"]))
        top.append(self.dd)
        reload_b = Gtk.Button(icon_name="view-refresh-symbolic", tooltip_text="Reload")
        reload_b.connect("clicked", lambda *_: self._load())
        top.append(reload_b)
        self.append(top)
        self.text = Gtk.TextView(editable=False, monospace=True, css_classes=["card"])
        sc = Gtk.ScrolledWindow(vexpand=True)
        sc.set_child(self.text)
        self.append(sc)
        self._files = []

    def refresh(self, data: dict) -> None:
        logdir = os.path.expanduser("~/.bhserve/logs")
        self._files = sorted(os.listdir(logdir)) if os.path.isdir(logdir) else []
        model = Gtk.StringList.new(self._files or ["(no logs yet)"])
        self.dd.set_model(model)
        if self._files:
            self._load()

    def _load(self):
        if not self._files:
            return
        idx = self.dd.get_selected()
        if idx < 0 or idx >= len(self._files):
            return
        path = os.path.expanduser(f"~/.bhserve/logs/{self._files[idx]}")
        try:
            with open(path, errors="replace") as f:
                content = "".join(f.readlines()[-500:])
        except Exception as e:
            content = str(e)
        self.text.get_buffer().set_text(content)


# ─────────────────────────────────────────────────────────────────────────────
class SettingsPage(Gtk.Box):
    def __init__(self, win) -> None:
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=16,
                         margin_top=18, margin_bottom=18, margin_start=18, margin_end=18)
        self.win = win
        sc = Gtk.ScrolledWindow(vexpand=True)
        body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=18)
        sc.set_child(body)
        self.append(sc)

        g1 = Adw.PreferencesGroup(title="Startup and updates")
        self.autostart = Adw.SwitchRow(title="Start BHServe at login", subtitle="systemd user service")
        self.autostart.connect("notify::active", self._toggle_autostart)
        g1.add(self.autostart)
        self.autoupdate = Adw.SwitchRow(title="Check for updates automatically",
                                        active=self.win.cfg_bool("auto_update", True))
        self.autoupdate.connect("notify::active",
                                lambda r, _p: self.win.set_cfg("auto_update", r.get_active()))
        g1.add(self.autoupdate)
        check_row = Adw.ActionRow(title="Check for updates now",
                                  subtitle=f"Current version {self.win.app_version}")
        check_btn = Gtk.Button(label="Check", valign=Gtk.Align.CENTER)
        check_btn.connect("clicked", lambda *_: self.win.check_updates(force=True))
        check_row.add_suffix(check_btn)
        g1.add(check_row)
        body.append(g1)

        g2 = Adw.PreferencesGroup(title="List sizes", description="Rows per page in each list")
        self.sizes = {}
        for key, label in (("sites_page_size", "Sites"), ("databases_page_size", "Databases"),
                           ("apps_page_size", "Node / Python apps")):
            r = Adw.ComboRow(title=label, model=Gtk.StringList.new(PAGE_SIZES))
            cur = self.win.cfg_int(key, 15)
            try:
                r.set_selected(PAGE_SIZES.index("All" if cur >= 10 ** 8 else str(cur)))
            except ValueError:
                r.set_selected(1)
            r.connect("notify::selected", lambda row, _p, k=key: self.win.set_cfg(
                k, page_size_to_int(PAGE_SIZES[row.get_selected()])))
            self.sizes[key] = r
            g2.add(r)
        body.append(g2)

        g3 = Adw.PreferencesGroup(title="Defaults for new sites")
        self.default_php = Adw.ComboRow(title="Default PHP", model=Gtk.StringList.new(
            [k.replace("php@", "") for k in PHP_KEYS]))
        self.default_php.connect("notify::selected", lambda row, _p: self.win.engine.run(
            "config", "set", "default_php", [k.replace("php@", "") for k in PHP_KEYS][row.get_selected()]))
        g3.add(self.default_php)
        body.append(g3)

        g4 = Adw.PreferencesGroup(title="About")
        about = Adw.ActionRow(title="BHServe for Linux",
                              subtitle=f"Version {self.win.app_version} · biswashost.com",
                              activatable=True)
        about.add_suffix(Gtk.Image.new_from_icon_name("help-about-symbolic"))
        about.connect("activated", lambda *_: self.win.about())
        g4.add(about)
        body.append(g4)

    def refresh(self, data: dict) -> None:
        self.autostart.set_active(bool(data.get("loginitem")))
        dphp = data.get("config", {}).get("default_php", "8.4")
        try:
            self.default_php.set_selected([k.replace("php@", "") for k in PHP_KEYS].index(dphp))
        except ValueError:
            pass

    def _toggle_autostart(self, row, _p):
        self.win.run_verb(["loginitem", "enable" if row.get_active() else "disable"], None, refresh=False)
