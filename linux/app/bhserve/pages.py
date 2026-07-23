"""The 8 panes (parity with macOS v1.7.4): Dashboard / Services / Sites / Databases /
Node / Python / Logs / Settings. Each Page is a Gtk.Box with a refresh(api) method the
window calls after every `bhserve api` snapshot.
"""
from __future__ import annotations

import json
import os
import re
import shlex
import shutil
import subprocess
from collections import deque
from typing import Callable

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, Gio, GLib, Gtk, Pango  # noqa: E402

from .metrics import CpuSampler, NetSampler, disk, memory, rate_str  # noqa: E402
from .widgets import PAGE_SIZES, PagedList, page_size_to_int, pill, status_dot  # noqa: E402

# The CPU sparkline uses a Cairo draw callback, which needs the cairo↔Python foreign-struct
# converter (the python3-gi-cairo package). If it's missing, drawing throws in the marshaller
# *before* our code runs — so detect it up front and skip the sparkline rather than flood errors.
try:
    gi.require_foreign("cairo")
    _HAVE_CAIRO = True
except Exception:
    _HAVE_CAIRO = False

PHP_KEYS = ["php@8.6", "php@8.5", "php@8.4", "php@8.3", "php@8.2", "php@8.1", "php@8.0", "php@7.4"]
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


# ── shared site row (used by both the Sites pane and the Dashboard websites panel) ──
TOOL_NAMES = {"phpmyadmin", "adminer", "mailpit"}


def is_tool(name: str) -> bool:
    return name in TOOL_NAMES


def site_match(s: dict, q: str) -> bool:
    q = q.lower()
    return q in s["name"].lower() or q in s.get("php", "").lower()


def _is_ols(s: dict) -> bool:
    """OLS-backed sites need root for their config resync + reload ($SUDO cp into
    /usr/local/lsws/conf + systemctl) — normally-unprivileged site verbs (php/subdomain)
    must run privileged for them, or the change silently never reaches OLS."""
    return s.get("server") in ("ols", "openlitespeed")


def site_change_php(win, s: dict) -> None:
    installed = [x["key"].replace("php@", "") for x in win.last_data.get("services", [])
                 if x["role"] == "php" and x["installed"]]
    win.choose("Change PHP version", f"Pick a PHP version for {s['name']}", installed,
               lambda v: win.run_verb(["site", "php", s["name"], v], f"Switching {s['name']} → PHP {v}…",
                                      force_root=_is_ols(s)))


# Web servers a site can be switched to, in menu order. Only the ones the site is NOT
# currently using are shown (see _site_menu) — you never see "Switch to X" for the X you're on.
_WEB_SERVERS = [("nginx", "Switch to nginx"),
                ("apache", "Switch to Apache"),
                ("ols", "Switch to OpenLiteSpeed")]


def _site_menu(win, s: dict) -> Gtk.Popover:
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
    item("Change PHP version…", "application-x-php-symbolic", lambda: site_change_php(win, s))
    # Direct "Switch to <other server>" items — hide the one this site already runs.
    _cur = s.get("server", "nginx")
    _cur = "ols" if _cur in ("ols", "openlitespeed") else _cur
    for _val, _label in _WEB_SERVERS:
        if _val == _cur:
            continue
        item(_label, "network-server-symbolic",
             lambda v=_val: win.run_verb(["site", "server", name, v], f"Switching {name} → {v}…"))
    dom = s["domain"]
    if not s.get("secure"):
        item("Install SSL (HTTPS)", "security-high-symbolic",
             lambda: win.run_verb(["secure", dom], f"Securing {dom}…"))
    else:
        item("Reinstall SSL (fresh certificate)", "security-high-symbolic",
             lambda: win.run_verb(["resecure", dom], f"Reinstalling SSL for {dom}…"))
        item("Remove SSL", "security-low-symbolic",
             lambda: win.confirm(f"Remove SSL from “{dom}”?",
                                 "The site keeps working over http://.",
                                 lambda: win.run_verb(["unsecure", dom], f"Removing SSL from {dom}…")),
             destructive=True)
    if s.get("tunnel"):
        item("Sharing publicly — manage…", "network-transmit-receive-symbolic",
             lambda: win.site_share(name))
    else:
        item("Share publicly (Cloudflare)", "network-wireless-symbolic",
             lambda: win.site_share(name))
    item("Manage subdomains…", "network-server-symbolic", lambda: site_subdomains(win, s))
    item("Open folder", "folder-symbolic", lambda: _open(s["root"]))
    item("Open in editor", "text-editor-symbolic", lambda: _open_editor(s["root"]))
    item("Open terminal", "utilities-terminal-symbolic", lambda: _open_terminal(s["root"]))
    srv = s.get("server", "nginx")
    _conf_path = f"{os.path.expanduser('~/.bhserve')}/{'apache' if srv == 'apache' else 'nginx'}/sites/{name}.conf"
    item(f"Open {'Apache' if srv == 'apache' else 'nginx'} config", "text-editor-symbolic",
         lambda: _open(_conf_path))
    item("Delete site…", "user-trash-symbolic", lambda: win.confirm(
        f"Delete site “{name}”?", "Removes the vhost. Tick purge in the next step to also drop files + DB.",
        lambda: win.run_verb(["site", "rm", name], f"Removing {name}…")), destructive=True)
    # Bound the popover height: this menu has ~11 items (~420px). On a short window, or for the
    # bottom rows of the list, a popover that tall can't fit below the button AND can't flip above
    # → GTK gives up and it never appears ("last 2 sites, 3-dot does nothing"). A max-height scroller
    # keeps it placeable everywhere (GTK flips up/down freely) and it scrolls internally if too tall.
    scroller = Gtk.ScrolledWindow(propagate_natural_height=True, propagate_natural_width=True)
    scroller.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
    scroller.set_max_content_height(360)
    scroller.set_child(v)
    pop.set_child(scroller)
    return pop


def build_site_row(win, s: dict) -> Adw.ActionRow:
    scheme = "https" if s.get("secure") else "http"
    row = Adw.ActionRow(title=s["domain"],
                        subtitle=f"{s.get('php','')} · {s.get('server','nginx')} · {scheme}")
    row.add_prefix(status_dot(s.get("enabled", True)))
    box = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6, valign=Gtk.Align.CENTER)
    if s.get("secure"):
        box.append(pill("HTTPS", "bh-pill-blue"))
    if s.get("tunnel"):
        shared = pill("SHARED", "bh-pill-warn")
        shared.set_tooltip_text(f"Public: {s['tunnel']}")
        box.append(shared)
    aliases = s.get("aliases") or []
    if aliases:
        box.append(pill(f"{len(aliases)} aliases", "bh-pill-warn"))
    openb = Gtk.Button(icon_name="web-browser-symbolic", tooltip_text="Open in browser")
    openb.connect("clicked", lambda *_: _open(f"{scheme}://{s['domain']}"))
    box.append(openb)
    menu = Gtk.MenuButton(icon_name="view-more-symbolic", tooltip_text="More")
    menu.set_popover(_site_menu(win, s))
    box.append(menu)
    row.add_suffix(box)
    return row


def site_subdomains(win, s: dict) -> None:
    dlg = Adw.MessageDialog(transient_for=win, heading=f"Subdomains for {s['domain']}",
                            body="Add or remove subdomain aliases for this site.")
    form = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
    entry = Gtk.Entry(placeholder_text=f"api or api.{s['domain']}", hexpand=True)
    add = Gtk.Button(label="Add", icon_name="list-add-symbolic", css_classes=["suggested-action"], valign=Gtk.Align.CENTER)
    row_input = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
    row_input.append(entry); row_input.append(add)
    form.append(row_input)

    list_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=0, css_classes=["card"])
    form.append(list_box)

    def redraw():
        while list_box.get_first_child():
            list_box.remove(list_box.get_first_child())
        aliases = next((x.get("aliases", []) for x in win.last_data.get("sites", []) if x.get("name") == s["name"]), s.get("aliases", []))
        if not aliases:
            lbl = Gtk.Label(label="No subdomains yet.", xalign=0, css_classes=["dim-label"],
                            margin_top=10, margin_bottom=10, margin_start=12)
            list_box.append(lbl)
            return
        scheme = "https" if s.get("secure") else "http"
        for i, alias in enumerate(aliases):
            line = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8,
                           margin_top=8, margin_bottom=8, margin_start=12, margin_end=8)
            link = Gtk.Button(label=f"{scheme}://{alias}", css_classes=["flat"], hexpand=True, halign=Gtk.Align.START)
            link.connect("clicked", lambda *_args, a=alias: _open(f"{scheme}://{a}"))
            line.append(link)
            rm = Gtk.Button(icon_name="user-trash-symbolic", tooltip_text="Remove",
                            css_classes=["flat", "destructive-action"])
            rm.connect("clicked", lambda *_args, a=alias: win.run_verb(["site", "subdomain", "rm", s["name"], a], f"Removing {a}…",
                                                                       force_root=_is_ols(s)))
            line.append(rm)
            list_box.append(line)
            if i < len(aliases) - 1:
                sep = Gtk.Separator(orientation=Gtk.Orientation.HORIZONTAL)
                list_box.append(sep)

    def add_alias(*_):
        val = entry.get_text().strip()
        if val:
            entry.set_text("")
            win.run_verb(["site", "subdomain", "add", s["name"], val], "Adding subdomain…",
                         force_root=_is_ols(s))

    add.connect("clicked", add_alias)
    entry.connect("activate", add_alias)
    redraw()
    dlg.set_extra_child(form)
    dlg.add_response("close", "Close")
    dlg.present()


# ─────────────────────────────────────────────────────────────────────────────
def _set_dot(img: Gtk.Image, on: bool) -> None:
    img.remove_css_class("dot-on")
    img.remove_css_class("dot-off")
    img.add_css_class("dot-on" if on else "dot-off")


class CardGrid(Gtk.Grid):
    """Responsive grid of equal-width cards — the GTK equivalent of the Windows dashboard's
    4×`*`-column Grid: every card is STRETCHED to an equal share of the width (so 4 always
    fill a row, never 3+1), and the column count reflows 4 → 2 → 1 as the window narrows.
    A FlowBox can't do this (it sizes to the widest card's natural width, not a forced N)."""
    __gtype_name__ = "BHCardGrid"

    def __init__(self) -> None:
        super().__init__(column_spacing=12, row_spacing=12, column_homogeneous=True, hexpand=True)
        self._cards: list[Gtk.Widget] = []
        self._cols = 0

    def add_card(self, w: Gtk.Widget) -> None:
        w.set_hexpand(True)
        w.set_halign(Gtk.Align.FILL)
        i = len(self._cards)
        self._cards.append(w)
        self.attach(w, i % 4, i // 4, 1, 1)   # provisional 4-col (a sane default); do_size_allocate refines

    def do_measure(self, orientation, for_size):
        minimum, natural, min_b, nat_b = Gtk.Grid.do_measure(self, orientation, for_size)
        if orientation == Gtk.Orientation.HORIZONTAL and self._cards:
            # A homogeneous N-col Grid reports its FULL N-column min width as its minimum. That pins
            # the whole window wide and PREVENTS the narrowing that would trigger the 4→2→1 reflow
            # (chicken-and-egg: can't shrink to reflow because the un-reflowed min blocks shrinking).
            # Report a SINGLE card's min instead — the window can then narrow, do_size_allocate fires
            # with the smaller width, and we reflow to fit. Cards wrap/ellipsize so they never clip.
            one = 0
            for c in self._cards:
                cm = c.measure(Gtk.Orientation.HORIZONTAL, -1)[0]
                if cm > one:
                    one = cm
            return (one, natural, min_b, nat_b)
        return (minimum, natural, min_b, nat_b)

    def do_size_allocate(self, width: int, height: int, baseline: int) -> None:
        cols = 4 if width >= 700 else 2 if width >= 360 else 1
        if cols != self._cols:
            self._cols = cols
            GLib.idle_add(self._relayout, cols)          # defer: never mutate layout inside allocate
        Gtk.Grid.do_size_allocate(self, width, height, baseline)

    def _relayout(self, cols: int) -> bool:
        for c in list(self._cards):
            if c.get_parent() is self:
                self.remove(c)
        for i, c in enumerate(self._cards):
            self.attach(c, i % cols, i // cols, 1, 1)
        return False


class DashboardPage(Gtk.Box):
    """Parity with the macOS/Windows dashboard: Start/Stop/Restart-all, status cards
    (Web/PHP/DB/Cache), CPU sparkline + Memory/Disk/Network, the websites panel, the
    web-tools toggles, and an activity log."""

    def __init__(self, win) -> None:
        super().__init__(orientation=Gtk.Orientation.VERTICAL)
        self.win = win
        self.cpu = CpuSampler()
        self.net = NetSampler()
        self.cpu_hist: deque = deque(maxlen=40)
        self._loading_tools = False

        scroller = Gtk.ScrolledWindow(vexpand=True, hexpand=True)
        # NEVER horizontal-scroll: otherwise the ScrolledWindow lets the content keep its wide
        # natural width and just adds an h-scrollbar, so the cards never receive the narrowed
        # width and never reflow. NEVER forces the page width down onto the content.
        scroller.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=16,
                       margin_top=18, margin_bottom=18, margin_start=18, margin_end=18)
        scroller.set_child(body)
        self.append(scroller)

        # ── header: title + subtitle + global buttons ──
        head = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=12)
        tb = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, hexpand=True)
        tb.append(Gtk.Label(label="Dashboard", xalign=0, css_classes=["title-1"]))
        self.subtitle = Gtk.Label(label="", xalign=0, css_classes=["dim-label"])
        tb.append(self.subtitle)
        head.append(tb)
        self.start_btn = Gtk.Button(label="Start all", icon_name="media-playback-start-symbolic", valign=Gtk.Align.CENTER)
        self.start_btn.connect("clicked", lambda *_: self.win.run_verb(["start", "all"], "Starting all services…"))
        self.stop_btn = Gtk.Button(label="Stop all", icon_name="media-playback-stop-symbolic", valign=Gtk.Align.CENTER)
        self.stop_btn.connect("clicked", lambda *_: self.win.run_verb(["stop", "all"], "Stopping all services…"))
        self.restart_btn = Gtk.Button(label="Restart", icon_name="view-refresh-symbolic", valign=Gtk.Align.CENTER)
        self.restart_btn.connect("clicked", lambda *_: self.win.run_verb(["restart", "all"], "Restarting all services…"))
        for b in (self.start_btn, self.stop_btn, self.restart_btn):
            head.append(b)
        body.append(head)

        # ── all 8 cards in ONE responsive grid: Web/PHP/DB/Cache + CPU/Mem/Storage/Net,
        #    laid out 4+4 on wide, 2+2+2+2 on medium, stacked on narrow ──
        cards = CardGrid()
        self.c_web = self._status_card("Web Server")
        self.c_php = self._status_card("PHP")
        self.c_db = self._status_card("Database")
        self.c_cache = self._status_card("Cache")
        self.cpu_val, cpu_card = self._cpu_card()
        self.mem = self._bar_card("Memory")
        self.disk = self._bar_card("Storage")
        self.net_down, self.net_up, net_card = self._net_card()
        for c in (self.c_web["card"], self.c_php["card"], self.c_db["card"], self.c_cache["card"],
                  cpu_card, self.mem["card"], self.disk["card"], net_card):
            cards.add_card(c)
        body.append(cards)

        # ── websites panel ──
        self.web_header = Gtk.Label(label="Websites", xalign=0, css_classes=["title-4"])
        body.append(self.web_header)
        self.site_list = PagedList(lambda s: build_site_row(self.win, s), site_match,
                                   page_size=self.win.cfg_int("dashboard_page_size", 5),
                                   empty_text="No sites yet — add one from the Sites tab.",
                                   on_page_size_changed=lambda n: self.win.set_cfg("dashboard_page_size", n),
                                   scroll=False)
        body.append(self.site_list)

        # ── web tools ──
        body.append(Gtk.Label(label="Web tools", xalign=0, css_classes=["title-4"]))
        tools = CardGrid()
        self.t_pma = self._tool_card("phpMyAdmin", "phpmyadmin", ["pma", "install"])
        self.t_adm = self._tool_card("Adminer", "adminer", ["adminer", "install"])
        self.t_mail = self._tool_card("Mailpit", "mailpit", ["mailpit", "setup"])
        for t in (self.t_pma, self.t_adm, self.t_mail):
            tools.add_card(t["card"])
        body.append(tools)

        # ── activity log ──
        self.log_expander = Gtk.Expander(label="Activity log")
        self.log_view = Gtk.TextView(editable=False, monospace=True, css_classes=["card"])
        log_sc = Gtk.ScrolledWindow(min_content_height=150)
        log_sc.set_child(self.log_view)
        self.log_expander.set_child(log_sc)
        body.append(self.log_expander)

    # ── card builders ──
    def _status_card(self, title):
        card = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=3, css_classes=["card", "bh-metric"])
        top = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL)
        top.append(Gtk.Label(label=title, xalign=0, hexpand=True, css_classes=["bh-metric-cap", "dim-label"]))
        dot = status_dot(False)
        top.append(dot)
        card.append(top)
        # Wrap (don't stretch) a long value like the PHP-versions list, so it can't inflate the card
        # width and break the 4-per-row grid; it flows to a 2nd line inside the fixed-width card.
        val = Gtk.Label(label="—", xalign=0, css_classes=["bh-metric-val"], wrap=True,
                        wrap_mode=Pango.WrapMode.WORD_CHAR, max_width_chars=18)
        sub = Gtk.Label(label="", xalign=0, css_classes=["dim-label"])
        card.append(val)
        card.append(sub)
        return {"card": card, "val": val, "sub": sub, "dot": dot}

    def _cpu_card(self):
        card = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=3, css_classes=["card", "bh-metric"])
        card.append(Gtk.Label(label="CPU", xalign=0, css_classes=["bh-metric-cap", "dim-label"]))
        val = Gtk.Label(label="0%", xalign=0, css_classes=["bh-metric-val"])
        card.append(val)
        self.spark = Gtk.DrawingArea(content_height=34, hexpand=True)
        if _HAVE_CAIRO:
            self.spark.set_draw_func(self._draw_spark)
        card.append(self.spark)
        return val, card

    def _bar_card(self, title):
        card = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=3, css_classes=["card", "bh-metric"])
        card.append(Gtk.Label(label=title, xalign=0, css_classes=["bh-metric-cap", "dim-label"]))
        val = Gtk.Label(label="—", xalign=0, css_classes=["bh-metric-val"])
        card.append(val)
        bar = Gtk.ProgressBar()
        card.append(bar)
        return {"card": card, "val": val, "bar": bar}

    def _net_card(self):
        card = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=3, css_classes=["card", "bh-metric"])
        card.append(Gtk.Label(label="Network", xalign=0, css_classes=["bh-metric-cap", "dim-label"]))
        down = Gtk.Label(label="Down  —", xalign=0)
        up = Gtk.Label(label="Up  —", xalign=0)
        card.append(down)
        card.append(up)
        return down, up, card

    def _tool_card(self, title, site_name, on_verb):
        card = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6, css_classes=["card", "bh-metric"])
        card.append(Gtk.Label(label=title, xalign=0, css_classes=["bh-metric-cap"]))
        status = Gtk.Label(label="Off", xalign=0, css_classes=["dim-label"])
        card.append(status)
        bottom = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        sw = Gtk.Switch(valign=Gtk.Align.CENTER)
        sw.connect("notify::active", lambda s, _p, n=site_name, v=on_verb: self._tool_toggled(n, v, s.get_active()))
        bottom.append(sw)
        bottom.append(Gtk.Label(label="", hexpand=True))
        openb = Gtk.Button(label="Open", valign=Gtk.Align.CENTER)
        openb.connect("clicked", lambda *_, t=site_name: self._tool_open(t))
        bottom.append(openb)
        card.append(bottom)
        return {"card": card, "switch": sw, "status": status, "open": openb, "url": ""}

    # ── drawing ──
    def _draw_spark(self, area, cr, w, h):
        pts = list(self.cpu_hist)
        if len(pts) < 2:
            return
        cr.set_source_rgb(0.051, 0.431, 0.992)  # #0d6efd
        cr.set_line_width(2)
        n = len(pts)
        for i, v in enumerate(pts):
            x = i * w / (n - 1)
            y = h - (v / 100.0) * (h - 2) - 1
            cr.line_to(x, y) if i else cr.move_to(x, y)
        cr.stroke()

    # ── web tools ──
    def _tool_toggled(self, name, on_verb, on):
        if self._loading_tools:
            return
        args = on_verb if on else ["site", "rm", name]
        self.win.run_verb(args, f"{'Enabling' if on else 'Disabling'} {name}…")

    def _tool_open(self, name):
        t = {"phpmyadmin": self.t_pma, "adminer": self.t_adm, "mailpit": self.t_mail}[name]
        if t["url"]:
            _open(t["url"])

    def _set_tool(self, t, sites_all, name):
        site = next((s for s in sites_all if s["name"].lower() == name), None)
        active = bool(site and site.get("enabled", True))
        t["switch"].set_active(active)
        t["open"].set_sensitive(active)
        secure = bool(site and site.get("secure"))
        t["status"].set_label(("Active · https" if secure else "Active") if active else "Off")
        t["url"] = ((("https://" if secure else "http://") + site["domain"]) if active and site else "")

    # ── refresh ──
    def refresh(self, data: dict) -> None:
        services = data.get("services", [])
        all_sites = data.get("sites", [])
        run = lambda k: any(s["key"] == k and s.get("running") for s in services)  # noqa: E731

        php_vers = sorted([s["key"][4:] for s in services
                           if s["role"] == "php" and s["key"].startswith("php@") and s["installed"]], reverse=True)
        sites = sorted([s for s in all_sites if not is_tool(s["name"])], key=lambda s: s["name"])

        nginx, apache = run("nginx"), run("httpd")
        web = "nginx + apache" if (nginx and apache) else "nginx" if nginx else "apache" if apache else "nginx"
        self._fill(self.c_web, web, f"{len(sites)} site{'' if len(sites) == 1 else 's'}", nginx or apache)
        self._fill(self.c_php, ", ".join(php_vers) if php_vers else "not installed",
                   f"{len(php_vers)} installed", any(s["role"] == "php" and s.get("running") for s in services))
        maria, my, pg = run("mariadb"), run("mysql"), run("postgresql@16") or run("postgresql")
        dbrun = maria or my or pg
        self._fill(self.c_db, "MariaDB" if maria else "MySQL" if my else "PostgreSQL" if pg else "MySQL / MariaDB",
                   "running" if dbrun else "stopped", dbrun)
        redis, memc = run("redis"), run("memcached")
        self._fill(self.c_cache, "Redis · Memcached",
                   f"redis {'on' if redis else 'off'}, memcached {'on' if memc else 'off'}", redis or memc)

        self.subtitle.set_label(f"{sum(1 for s in services if s.get('running'))} services running · {len(sites)} sites")

        cpu = self.cpu.percent()
        self.cpu_val.set_label(f"{cpu:.0f}%")
        self.cpu_hist.append(cpu)
        if _HAVE_CAIRO:
            self.spark.queue_draw()
        mu, mt, mp = memory()
        self.mem["val"].set_label(f"{mu:.1f} / {mt:.1f} GB")
        self.mem["bar"].set_fraction(min(1.0, mp / 100))
        du, dt, dp = disk()
        self.disk["val"].set_label(f"{du:.0f} / {dt:.0f} GB")
        self.disk["bar"].set_fraction(min(1.0, dp / 100))
        down, up = self.net.rate_kbps()
        self.net_down.set_label(f"Down  {rate_str(down)}")
        self.net_up.set_label(f"Up  {rate_str(up)}")

        daemons = {"nginx", "httpd", "mariadb", "postgresql@16", "redis", "memcached", "mailpit"}
        any_running = any(s.get("running") for s in services)
        to_start = any(s["key"] in daemons and s["installed"] and s.get("enabled") and not s.get("running")
                       for s in services)
        self.start_btn.set_sensitive(to_start)
        self.stop_btn.set_sensitive(any_running)
        self.restart_btn.set_sensitive(any_running)
        for b in (self.start_btn, self.stop_btn):
            b.remove_css_class("suggested-action")
        if to_start:
            self.start_btn.add_css_class("suggested-action")
        elif any_running:
            self.stop_btn.add_css_class("suggested-action")

        self.web_header.set_label(f"Websites ({len(sites)})")
        self.site_list.set_items(sites)

        self._loading_tools = True
        self._set_tool(self.t_pma, all_sites, "phpmyadmin")
        self._set_tool(self.t_adm, all_sites, "adminer")
        self._set_tool(self.t_mail, all_sites, "mailpit")
        self._loading_tools = False

        log = "\n".join(getattr(self.win, "applog", [])[-200:])
        if self.log_view.get_buffer().get_char_count() != len(log):
            self.log_view.get_buffer().set_text(log)

    def _fill(self, card, val, sub, on):
        card["val"].set_label(val)
        card["sub"].set_label(sub)
        _set_dot(card["dot"], on)


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
        # Only rebuild when something actually changed — otherwise the 4s auto-refresh would
        # tear down + re-add every row and snap your scroll position back to the top.
        sig = tuple((s["key"], s["installed"], s.get("running"), s.get("enabled"), s.get("version", ""))
                    for s in services)
        if sig == getattr(self, "_sig", None):
            return
        self._sig = sig
        adj = self.scroller.get_vadjustment()
        pos = adj.get_value() if adj else 0.0
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
        if adj and pos:   # restore scroll after the new content is laid out
            GLib.idle_add(lambda a=adj, p=pos: (a.set_value(p), False)[1])

    def _row(self, s: dict) -> Adw.ActionRow:
        installed, running = s["installed"], s.get("running")
        sub = clean_version(s.get("version", "")) or s.get("formula", "")
        row = Adw.ActionRow(title=s["key"], subtitle=sub)
        row.add_prefix(status_dot(running))

        box = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6, valign=Gtk.Align.CENTER)
        key = s["key"]
        if not installed:
            b = Gtk.Button(label="Install", css_classes=["suggested-action"])
            b.connect("clicked", lambda *_, k=key: self.win.run_progress(
                ["install", k], f"Installing {k}",
                f"Downloading and setting up {k} — this can take a minute for larger packages…",
                f"{k} installed."))
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
            star = Gtk.ToggleButton(icon_name="starred-symbolic", tooltip_text="Auto-start",
                                    active=s.get("enabled", False), css_classes=["bh-star"])
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
        self.list = PagedList(lambda s: build_site_row(self.win, s), site_match,
                              page_size=self.win.cfg_int("sites_page_size", 15),
                              empty_text="No sites yet — click “Add site”.",
                              on_page_size_changed=lambda n: self.win.set_cfg("sites_page_size", n))
        self.append(self.list)

    def refresh(self, data: dict) -> None:
        # phpMyAdmin / Adminer / Mailpit are built-in tools, not user sites — they're managed
        # from Services + the dashboard web-tools, so keep them out of the Sites list.
        self.list.set_items([s for s in data.get("sites", []) if not is_tool(s.get("name", ""))])

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
        self.rt_btn = Gtk.Button(valign=Gtk.Align.CENTER, visible=False, css_classes=["suggested-action"])
        self._rt_handler_id = None
        self.rt_row.add_suffix(self.rt_btn)
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
            # skip blanks + the engine's "no X sites — bhserve … <name> …" help line (the <…>
            # placeholders also break GTK markup), usage lines, and *.test domains.
            low = line.lower()
            if (not line or "<" in line or "bhserve" in low or low.startswith("no ")
                    or "—" in line or "usage" in low or ".test" in line or len(line) <= 1):
                continue
            m = re.search(r"([a-z0-9][a-z0-9._-]*)", line, re.I)
            if m and m.group(1) not in ("python", "node", "site", "app"):
                apps.append({"name": m.group(1), "line": GLib.markup_escape_text(line)})
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

    def _set_runtime_btn(self, label, handler):
        if self._rt_handler_id:
            self.rt_btn.disconnect(self._rt_handler_id)
            self._rt_handler_id = None
        if not label:
            self.rt_btn.set_visible(False)
            return
        self.rt_btn.set_label(label)
        self._rt_handler_id = self.rt_btn.connect("clicked", lambda *_: handler())
        self.rt_btn.set_visible(True)

    def refresh(self, data: dict) -> None:
        self.list.set_items(self._apps())


class NodePage(_AppsPage):
    KIND, TITLE = "node", "Node apps"

    def refresh(self, data):
        installed = any(s["key"] == "fnm" and s["installed"] for s in data.get("services", []))
        out = self.win.engine.run("node", "list")[1].strip() if installed else ""
        self.rt_row.set_title("Node (fnm)" if installed else "Node — fnm not installed")
        self.rt_row.set_subtitle((out[:80] if out else "Install a Node version to run Node apps")
                                 if installed else "Install fnm, then a Node version, to run Node apps")
        if installed:
            self._set_runtime_btn("Install Node version…", self._install_node)
        else:
            self._set_runtime_btn("Install fnm", lambda: self.win.run_progress(
                ["install", "fnm"], "Installing fnm", "Downloading the Node version manager…", "fnm installed."))
        super().refresh(data)

    def _install_node(self):
        self.win.choose("Install Node", "Pick a version to install and set as default:", ["22", "20", "18"],
                        lambda v: self.win.run_verb(["node", "install", v], f"Installing Node {v}…",
                                                    then=(["node", "use", v], f"Setting Node {v} as default…")))


class PythonPage(_AppsPage):
    KIND, TITLE = "py", "Python apps"

    def refresh(self, data):
        py = next((s for s in data.get("services", []) if s["key"] == "python"), {})
        inst = py.get("installed")
        self.rt_row.set_title("Python" + (f" {clean_version(py.get('version',''))}" if inst else " — not installed"))
        self.rt_row.set_subtitle("Ready for venv-backed apps" if inst else "Install Python to run Python apps")
        self._set_runtime_btn(None if inst else "Install Python",
                              lambda: self.win.run_progress(
                                  ["install", "python"], "Installing Python",
                                  "Setting up Python with venv support…", "Python installed."))
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
        self.list = PagedList(self._db_row,
                              lambda d, q: q.lower() in d.get("name", "").lower(),
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
        new = Adw.PreferencesGroup(title="Database servers")
        db_svcs = [x for x in data.get("services", []) if x["role"] == "db"]
        # root-password state (MySQL/MariaDB only) — one quick query when a server is up
        my_running = any(s["key"] in ("mariadb", "mysql") and s.get("running") for s in db_svcs)
        root_status = ""
        if my_running:
            _rc, out = self.win.engine.run("db", "root-status")
            root_status = (out.strip().splitlines()[-1].strip() if out.strip() else "")
        for s in db_svcs:
            key = s["key"]
            is_my = key in ("mariadb", "mysql")
            sub = clean_version(s.get("version", "")) or s["formula"]
            if is_my and s.get("running") and root_status in ("set", "blank"):
                sub += " · " + ("password set" if root_status == "set" else "no password")
            row = Adw.ActionRow(title=key, subtitle=sub)
            row.add_prefix(status_dot(s.get("running")))
            box = Gtk.Box(spacing=6, valign=Gtk.Align.CENTER)
            if not s["installed"]:
                b = Gtk.Button(label="Install", css_classes=["suggested-action"])
                b.connect("clicked", lambda _w, k=key: self.win.run_progress(
                    ["install", k], f"Installing {k}",
                    f"Downloading and setting up {k}. This can take a minute…", f"{k} installed."))
                box.append(b)
            else:
                verb = "stop" if s.get("running") else "start"
                icon = "media-playback-stop-symbolic" if s.get("running") else "media-playback-start-symbolic"
                b = Gtk.Button(icon_name=icon, tooltip_text=verb.title())
                b.connect("clicked", lambda _w, k=key, v=verb: self.win.run_verb([v, k], f"{v} {k}…"))
                box.append(b)
                if is_my and s.get("running"):
                    rb = Gtk.Button(label="Root password…", valign=Gtk.Align.CENTER)
                    rb.connect("clicked", lambda *_: self.win.db_root_dialog())
                    box.append(rb)
            row.add_suffix(box)
            new.add(row)
        parent = self.servers.get_parent()
        if parent:
            parent.remove(self.servers)
            parent.insert_child_after(new, None)
        self.servers = new

    def _db_row(self, item):
        name = item.get("name", "")
        engine = item.get("engine", "mysql")
        row = Adw.ActionRow(title=name,
                            subtitle="PostgreSQL" if engine == "pg" else "MariaDB / MySQL")
        box = Gtk.Box(spacing=6, valign=Gtk.Align.CENTER)
        if engine != "pg":
            pb = Gtk.Button(label="Password…", valign=Gtk.Align.CENTER)
            pb.connect("clicked", lambda *_, n=name: self.win.db_password_dialog(n))
            box.append(pb)
        drop = Gtk.Button(label="Drop", valign=Gtk.Align.CENTER, css_classes=["destructive-action"])
        drop.connect("clicked", lambda *_, n=name, e=engine: self.win.db_drop(n, e))
        box.append(drop)
        row.add_suffix(box)
        return row

    def _render_dbs(self, data):
        _rc, out = self.win.engine.run("db", "list", "--json")
        items = []
        if "[" in out:
            try:
                items = json.loads(out[out.find("["):out.rfind("]") + 1])
            except Exception:
                items = []
        self.list.set_items(items)


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
        self._syncing = False   # True while refresh() sets switch states — see _toggle_autostart
        sc = Gtk.ScrolledWindow(vexpand=True)
        body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=18)
        sc.set_child(body)
        self.append(sc)

        g1 = Adw.PreferencesGroup(title="Startup and updates")
        self.autostart = Adw.SwitchRow(title="Start BHServe at login",
                                       subtitle="Starts the services at boot + shows the tray at login")
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
        for key, label, dflt in (("dashboard_page_size", "Dashboard websites", 5),
                                  ("sites_page_size", "Sites", 15),
                                  ("databases_page_size", "Databases", 15),
                                  ("apps_page_size", "Node / Python apps", 15)):
            r = Adw.ComboRow(title=label, model=Gtk.StringList.new(PAGE_SIZES))
            cur = self.win.cfg_int(key, dflt)
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
        # ⚠️ Guard the programmatic set_active: the 4s api-refresh runs while a `loginitem enable`
        # is still waiting at the polkit prompt, so `loginitem` is still false → this would flip the
        # switch back OFF → re-fire notify::active → run `loginitem disable` = a SECOND password
        # prompt for the opposite action. The flag makes _toggle_autostart ignore our own updates.
        self._syncing = True
        self.autostart.set_active(bool(data.get("loginitem")))
        self._syncing = False
        dphp = data.get("config", {}).get("default_php", "8.4")
        try:
            self.default_php.set_selected([k.replace("php@", "") for k in PHP_KEYS].index(dphp))
        except ValueError:
            pass

    def _toggle_autostart(self, row, _p):
        if self._syncing:          # our own refresh() set the state — not a user click
            return
        self.win.run_verb(["loginitem", "enable" if row.get_active() else "disable"], None, refresh=False)
