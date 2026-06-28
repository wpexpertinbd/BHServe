"""Self-updater — polls the GitHub releases for the newest `linux-v*` tag, compares to
the running version, and (on request) downloads the .deb and installs it via pkexec apt.
Mirrors the Windows/Mac updaters, including the ≤1-call-per-30-min throttle (GitHub's
unauthenticated API is 60 req/hr/IP).
"""
from __future__ import annotations

import json
import os
import ssl
import subprocess
import tempfile
import time
import urllib.request
from urllib.parse import urlparse

from . import __version__


def _is_github_host(url: str) -> bool:
    """Only ever download/install assets served from GitHub's own hosts."""
    host = (urlparse(url).hostname or "").lower()
    return host == "github.com" or host.endswith(".githubusercontent.com")

REPO = "wpexpertinbd/BHServe"
TAG_PREFIX = "linux-v"
THROTTLE_FILE = os.path.expanduser("~/.bhserve/run/update-check.txt")
THROTTLE_SECONDS = 30 * 60


def _ver_tuple(v: str) -> tuple:
    out = []
    for part in v.strip().split("."):
        num = "".join(c for c in part if c.isdigit())
        out.append(int(num) if num else 0)
    return tuple(out) or (0,)


def is_newer(remote: str, local: str = __version__) -> bool:
    return _ver_tuple(remote) > _ver_tuple(local)


def throttle_due(seconds: int = THROTTLE_SECONDS) -> bool:
    try:
        return (time.time() - os.path.getmtime(THROTTLE_FILE)) >= seconds
    except OSError:
        return True


def stamp_throttle() -> None:
    try:
        os.makedirs(os.path.dirname(THROTTLE_FILE), exist_ok=True)
        with open(THROTTLE_FILE, "w") as f:
            f.write(str(int(time.time())))
    except OSError:
        pass


def latest_release() -> dict | None:
    """Newest linux-v* release → {version, deb_url, notes} or None."""
    url = f"https://api.github.com/repos/{REPO}/releases?per_page=30"
    try:
        ctx = ssl.create_default_context()
        req = urllib.request.Request(url, headers={"User-Agent": "BHServe-Linux"})
        with urllib.request.urlopen(req, timeout=15, context=ctx) as r:
            data = json.loads(r.read().decode())
    except Exception:
        return None
    best = None
    for rel in data:
        tag = rel.get("tag_name", "")
        if not tag.startswith(TAG_PREFIX) or rel.get("draft"):
            continue
        ver = tag[len(TAG_PREFIX):]
        deb = next((a["browser_download_url"] for a in rel.get("assets", [])
                    if a.get("name", "").endswith(".deb")
                    and _is_github_host(a.get("browser_download_url", ""))), None)
        cand = {"version": ver, "deb_url": deb, "notes": rel.get("body", "")}
        if best is None or is_newer(ver, best["version"]):
            best = cand
    return best


def check(force: bool = False) -> dict | None:
    """Return an update dict if a newer release exists, else None. Honours the throttle
    unless force=True (a manual 'Check now')."""
    if not force and not throttle_due():
        return None
    stamp_throttle()
    rel = latest_release()
    if rel and is_newer(rel["version"]) and rel.get("deb_url"):
        return rel
    return None


def download_and_install(deb_url: str, on_log=lambda s: None) -> tuple[bool, str]:
    """Download the .deb to a temp file and install it with pkexec apt (graphical sudo)."""
    if not _is_github_host(deb_url):
        return False, "Refusing to install: update is not hosted on GitHub."
    try:
        on_log("Downloading update…")
        ctx = ssl.create_default_context()
        req = urllib.request.Request(deb_url, headers={"User-Agent": "BHServe-Linux"})
        fd, path = tempfile.mkstemp(suffix=".deb", prefix="bhserve-")
        with urllib.request.urlopen(req, timeout=120, context=ctx) as r, os.fdopen(fd, "wb") as f:
            f.write(r.read())
        on_log("Installing (you may be asked for your password)…")
        # pkexec gives a graphical prompt; apt resolves any new deps.
        p = subprocess.run(["pkexec", "apt-get", "install", "-y", path],
                           capture_output=True, text=True)
        os.unlink(path) if os.path.exists(path) else None
        if p.returncode == 0:
            return True, "Update installed — restart BHServe to use the new version."
        return False, (p.stderr or p.stdout or "Install failed.").strip()[:300]
    except Exception as e:  # noqa: BLE001
        return False, str(e)
