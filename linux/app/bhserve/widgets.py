"""Shared GUI widgets — most importantly PagedList: the search + "Show N" + prev/next/
jump-to-page control that the Sites, Databases, Node and Python panes all reuse (the Linux
analog of the Mac's SitePaging / PerPagePicker / PageBar and the Windows RenderApps helper).
"""
from __future__ import annotations

from typing import Callable

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, Gtk  # noqa: E402

PAGE_SIZES = ["10", "15", "20", "50", "100", "All"]


def page_size_to_int(label: str) -> int:
    return 10 ** 9 if label == "All" else int(label)


class PagedList(Gtk.Box):
    """A vertical box: [ search-row | scrolled ListBox | pager-row ].

    The host page calls set_items(list); PagedList filters by the search text (using
    match_text), paginates by the chosen page size, and renders each visible item with
    make_row(item) -> Gtk.Widget.
    """

    def __init__(
        self,
        make_row: Callable[[object], Gtk.Widget],
        match_text: Callable[[object, str], bool],
        page_size: int = 15,
        empty_text: str = "Nothing here yet.",
        on_page_size_changed: Callable[[int], None] | None = None,
    ) -> None:
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        self._make_row = make_row
        self._match = match_text
        self._empty_text = empty_text
        self._on_size = on_page_size_changed
        self._all: list = []
        self._page = 0

        # ── search + page-size row ──
        top = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        self.search = Gtk.SearchEntry(hexpand=True, placeholder_text="Search…")
        self.search.connect("search-changed", self._on_search)
        top.append(self.search)
        top.append(Gtk.Label(label="Show", css_classes=["dim-label"]))
        self.size_dd = Gtk.DropDown.new_from_strings(PAGE_SIZES)
        try:
            self.size_dd.set_selected(PAGE_SIZES.index(str(page_size)))
        except ValueError:
            self.size_dd.set_selected(1)
        self.size_dd.connect("notify::selected", self._on_size_changed)
        top.append(self.size_dd)
        self.append(top)

        # ── the list ──
        self.listbox = Gtk.ListBox(
            selection_mode=Gtk.SelectionMode.NONE, css_classes=["boxed-list"]
        )
        scroller = Gtk.ScrolledWindow(vexpand=True, hexpand=True)
        scroller.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        scroller.set_child(self.listbox)
        self.append(scroller)

        self.empty = Adw.StatusPage(
            icon_name="folder-symbolic", title=empty_text, vexpand=True
        )
        self.empty.set_visible(False)
        self.append(self.empty)

        # ── pager ──
        self.pager = Gtk.Box(
            orientation=Gtk.Orientation.HORIZONTAL, spacing=8, halign=Gtk.Align.CENTER
        )
        self.prev_btn = Gtk.Button(icon_name="go-previous-symbolic")
        self.prev_btn.connect("clicked", lambda *_: self._goto(self._page - 1))
        self.page_lbl = Gtk.Label(label="Page 1 of 1", css_classes=["dim-label"])
        self.next_btn = Gtk.Button(icon_name="go-next-symbolic")
        self.next_btn.connect("clicked", lambda *_: self._goto(self._page + 1))
        self.jump = Gtk.SpinButton.new_with_range(1, 1, 1)
        self.jump.set_tooltip_text("Jump to page")
        self.jump.connect("value-changed", lambda sb: self._goto(int(sb.get_value()) - 1))
        for w in (self.prev_btn, self.page_lbl, self.next_btn, Gtk.Label(label="Go to"), self.jump):
            self.pager.append(w)
        self.append(self.pager)

    # ── public API ──
    def set_items(self, items: list) -> None:
        self._all = items or []
        self._render()

    def page_size(self) -> int:
        return page_size_to_int(PAGE_SIZES[self.size_dd.get_selected()])

    # ── internals ──
    def _on_search(self, *_):
        self._page = 0
        self._render()

    def _on_size_changed(self, *_):
        self._page = 0
        if self._on_size:
            self._on_size(self.page_size())
        self._render()

    def _goto(self, page: int) -> None:
        self._page = page
        self._render()

    def _filtered(self) -> list:
        q = self.search.get_text().strip()
        if not q:
            return self._all
        return [it for it in self._all if self._match(it, q)]

    def _render(self) -> None:
        items = self._filtered()
        size = self.page_size()
        pages = max(1, (len(items) + size - 1) // size)
        self._page = max(0, min(self._page, pages - 1))
        start = self._page * size
        page_items = items[start : start + size]

        child = self.listbox.get_first_child()
        while child:
            nxt = child.get_next_sibling()
            self.listbox.remove(child)
            child = nxt
        for it in page_items:
            self.listbox.append(self._make_row(it))

        has_rows = len(page_items) > 0
        self.listbox.set_visible(has_rows)
        self.empty.set_visible(not has_rows)
        self.empty.set_title(self._empty_text if not self._all else "No matches")
        self.pager.set_visible(pages > 1)
        self.page_lbl.set_label(f"Page {self._page + 1} of {pages}")
        self.prev_btn.set_sensitive(self._page > 0)
        self.next_btn.set_sensitive(self._page < pages - 1)
        self.jump.set_range(1, pages)
        self.jump.set_value(self._page + 1)


def status_dot(running: bool) -> Gtk.Image:
    img = Gtk.Image.new_from_icon_name(
        "media-record-symbolic" if running else "media-playback-stop-symbolic"
    )
    img.add_css_class("dot-on" if running else "dot-off")
    return img


def pill(text: str, css: str) -> Gtk.Label:
    lbl = Gtk.Label(label=text, css_classes=["bh-pill", css])
    lbl.set_valign(Gtk.Align.CENTER)
    return lbl
