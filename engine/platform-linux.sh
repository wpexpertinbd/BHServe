# shellcheck shell=bash
# BHServe — Linux / Debian platform delta layer  (apt + systemd)
# =============================================================================
# Sourced by ../engine/bhserve at the very end (just before the command dispatch)
# and ONLY when `uname` is Linux — so macOS is never affected by anything here.
#
# Strategy (see linux/engine/DELTAS.md): don't fork the engine. The macOS engine is
# already factored into small functions; here we redefine just the Homebrew/macOS
# surface so the SAME engine drives an apt-based Ubuntu/Debian stack:
#   • BHServe runs its OWN nginx + php-fpm processes (binaries from apt, all configs
#     under ~/.bhserve) — exactly like the Mac runs brew binaries with its own config.
#   • At install time it DISABLES the distro's systemd unit for any service it manages
#     itself (nginx/php-fpm/mariadb/…) so there's no :80/:443/:3306 collision.
#   • DB / cache / mail use systemd (the Linux analog of `brew services`).
#
# The single elegant lever: on Linux BREW_PREFIX="" — so the engine's hardcoded
# "$BREW_PREFIX/etc/nginx/mime.types" resolves to the real "/etc/nginx/mime.types",
# and "$BREW_PREFIX/<relative-probe>" resolves to a real "/usr/…" path. Only the few
# locators that point into a brew keg (nginx/php-fpm binaries) need an explicit override.
# =============================================================================

BREW_PREFIX=""
# Keep BREW non-empty so the engine's "[ -n "$BREW" ] || die "Homebrew not found"" guards
# pass; we never call brew (install/update/uninstall/doctor are overridden below).
BREW="linux"
export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:${PATH}"

# ── Elevation + invoking-user awareness ──────────────────────────────────────
# Privileged verbs are run by the GUI via `pkexec env BHSERVE_HOME=… bash <engine> <verb>`
# (a single polkit prompt per action) or from a terminal via `sudo bhserve …`. Either way
# we then run as root but must act ON BEHALF OF the desktop user: target THEIR ~/.bhserve,
# set workers/sockets to THEIR uid, and chown anything we create back to them on exit (so
# the user can still edit their own site files). No standing passwordless sudoers is created.
_BH_VERB="${1:-}"; _BH_SUB="${2:-}"
if [ "$(id -u)" = 0 ]; then
  _ru="${PKEXEC_UID:-${SUDO_UID:-}}"
  if [ -n "${_ru:-}" ] && [ "$_ru" != 0 ]; then
    USER_NAME="$(getent passwd "$_ru" | cut -d: -f1)"
    GROUP_NAME="$(id -gn "$USER_NAME" 2>/dev/null || echo "$USER_NAME")"
    export HOME="$(getent passwd "$_ru" | cut -d: -f6)"
    BH_HOME="${BHSERVE_HOME:-$HOME/.bhserve}"
  fi
  SUDO=""   # already root — no nested elevation
else
  SUDO="${BHSERVE_SUDO:-sudo}"
fi
# Hand ownership of anything we created as root back to the invoking user.
_bh_fix_ownership(){
  local _rc=$?   # preserve the real exit code — the GUI relies on it to detect failures
  { [ "$(id -u)" = 0 ] && [ -n "${USER_NAME:-}" ] && [ "$USER_NAME" != root ]; } || return $_rc
  [ -d "$BH_HOME" ] && chown -R "$USER_NAME":"$GROUP_NAME" "$BH_HOME" 2>/dev/null || true
  case "$_BH_VERB:$_BH_SUB" in
    site:add|pysite:add|nodesite:add)
      local sr; sr="$(jget sites_root "$HOME/BHServe/www" 2>/dev/null)"
      case "$sr" in "$HOME"/*) [ -d "$sr" ] && chown -R "$USER_NAME":"$GROUP_NAME" "$sr" 2>/dev/null || true ;; esac ;;
  esac
  return $_rc
}
trap _bh_fix_ownership EXIT

# ── Service registry ─────────────────────────────────────────────────────────
# key | apt package | version-probe binary (path WITHOUT leading slash, so
#       "$BREW_PREFIX/$probe" == "/usr/…") | role
# Note: no bare "php" row — on Debian every PHP is versioned (php-fpmX.Y); the engine's
# default_php (e.g. 8.4) already resolves to the "php@8.4" key, never bare "php".
services() {
  cat <<'EOF'
php@8.6|php8.6-fpm|usr/sbin/php-fpm8.6|php
php@8.5|php8.5-fpm|usr/sbin/php-fpm8.5|php
php@8.4|php8.4-fpm|usr/sbin/php-fpm8.4|php
php@8.3|php8.3-fpm|usr/sbin/php-fpm8.3|php
php@8.2|php8.2-fpm|usr/sbin/php-fpm8.2|php
php@8.1|php8.1-fpm|usr/sbin/php-fpm8.1|php
php@8.0|php8.0-fpm|usr/sbin/php-fpm8.0|php
php@7.4|php7.4-fpm|usr/sbin/php-fpm7.4|php
nginx|nginx|usr/sbin/nginx|web
httpd|apache2|usr/sbin/apache2|web
mariadb|mariadb-server|usr/sbin/mariadbd|db
mysql|mysql-server|usr/sbin/mysqld|db
postgresql@16|postgresql-16|usr/lib/postgresql/16/bin/postgres|db
redis|redis-server|usr/bin/redis-server|cache
memcached|memcached|usr/bin/memcached|cache
dnsmasq|dnsmasq|usr/sbin/dnsmasq|dns
mkcert|mkcert|usr/bin/mkcert|tls
mailpit|mailpit|usr/local/bin/mailpit|mail
fnm|fnm|usr/local/bin/fnm|node
python|python3-venv|usr/bin/python3|python
EOF
}

# MariaDB and MySQL both materialise /usr/sbin/mysqld (MariaDB as a symlink → it's the `mysql` row's
# probe), so a bare -x probe can't tell them apart, and they CONFLICT on Debian/Ubuntu (only one is
# installable at a time). Decide by package via dpkg; everything else falls back to the shared probe.
# (cmd_api now calls svc_installed, so this disambiguation reaches the GUI too.)
svc_installed(){
  case "$1" in
    mariadb) dpkg-query -W -f='${Status}\n' mariadb-server  2>/dev/null | grep -q 'ok installed' ;;
    mysql)   dpkg-query -W -f='${Status}\n' 'mysql-server*' 2>/dev/null | grep -q 'ok installed' ;;
    *)       local p; p="$(svc_probe "$1")"; [ -n "$p" ] && [ -x "$BREW_PREFIX/$p" ] ;;
  esac
}

# ── Binary locators (the few brew-keg paths that BREW_PREFIX="" can't fix) ────
NGINX_BIN(){ echo "/usr/sbin/nginx"; }
# fnm installs to /usr/local/bin (not on the merged-/usr path) — the engine's $BREW_PREFIX/bin
# would resolve to /bin/fnm and report "fnm not installed".
FNM_BIN(){ local f; for f in /usr/local/bin/fnm /usr/bin/fnm; do [ -x "$f" ] && { echo "$f"; return; }; done; echo /usr/local/bin/fnm; }
# Apache on Debian/Ubuntu is the apache2 binary; it needs its APACHE_* runtime vars set.
APACHE_BIN(){ echo "/usr/sbin/apache2"; }

# php-fpm is versioned on Debian: php-fpm8.4, php-fpm8.3, …  The bare "php" key maps
# to the configured default version.
php_fpm_bin(){
  local key="$1" v
  if [ "$key" = "php" ]; then v="$(jget default_php 8.4)"; v="${v#php@}"; else v="${key#php@}"; fi
  echo "/usr/sbin/php-fpm$v"
}

# Resolve a php key, but when resolving the DEFAULT (no explicit version) and the configured
# default isn't installed (e.g. registry default 8.4 on a release that only ships 8.5), fall back
# to the newest INSTALLED php so a `site add` without --php still gets a working pool. An EXPLICIT
# version is honoured as-is (so a deliberate choice isn't silently changed).
php_key(){
  local v="${1:-}" was_default=0
  case "$v" in ""|default) v="$(jget default_php 8.4)"; was_default=1 ;; esac
  case "$v" in php) echo php; return ;; php@*) v="${v#php@}" ;; esac
  if [ "$was_default" = 1 ] && [ ! -x "/usr/sbin/php-fpm$v" ]; then
    local nf; nf="$(ls /usr/sbin/php-fpm* 2>/dev/null | grep -oE '[0-9]+\.[0-9]+' | sort -rV | head -1)"
    [ -n "$nf" ] && v="$nf"
  fi
  echo "php@$v"
}

# ── Portable static PHP (static-php-cli · dl.static-php.dev) ──────────────────
# The hybrid fallback for PHP versions the distro/Ondřej can't provide (e.g. 8.4 on a brand-new
# Ubuntu that only ships 8.5). These are FULLY STATIC php-fpm binaries (no system libs) so they run
# on any release. We install one to /usr/local/lib/bhserve/php/<v>/php-fpm and symlink it in at the
# SAME /usr/sbin/php-fpm<v> path the distro uses — so detection, version-probe, pool rendering and
# serving all work with zero further changes.
_STATIC_PHP_BASE="https://dl.static-php.dev/static-php-cli/common"
_static_php_arch(){ case "$(uname -m)" in aarch64|arm64) echo aarch64 ;; *) echo x86_64 ;; esac; }
_static_php_install(){
  local v="$1" arch file tmp bin sysdir
  arch="$(_static_php_arch)"
  # Never clobber a real distro php-fpm binary — if one is already at the standard path (and it's
  # not our own symlink), the distro provides this version; nothing to do.
  if [ -e "/usr/sbin/php-fpm$v" ] && ! _is_static_php "$v"; then
    ok "PHP $v already provided by the distro package"; return 0
  fi
  hdr "Installing PHP $v  (portable static build)"
  file="$(curl -fsSL "$_STATIC_PHP_BASE/" 2>/dev/null \
          | grep -oE "php-$v\.[0-9]+-fpm-linux-$arch\.tar\.gz" | sort -uV | tail -1)"
  [ -n "$file" ] || { no "no portable PHP $v build available for $arch"; return 1; }
  sysdir="/usr/local/lib/bhserve/php/$v"; tmp="$(mktemp -d)"
  if curl -fsSL "$_STATIC_PHP_BASE/$file" -o "$tmp/fpm.tgz" \
     && tar xzf "$tmp/fpm.tgz" -C "$tmp" 2>/dev/null \
     && bin="$(find "$tmp" -type f -name 'php-fpm' | head -1)" && [ -n "$bin" ]; then
    $SUDO mkdir -p "$sysdir"
    $SUDO install -m 0755 "$bin" "$sysdir/php-fpm"
    $SUDO ln -sf "$sysdir/php-fpm" "/usr/sbin/php-fpm$v"
    rm -rf "$tmp"; ok "PHP $v installed (portable · $file)"; return 0
  fi
  rm -rf "$tmp"; no "portable PHP $v download/extract failed"; return 1
}
# True when php-fpm<v> is a portable static build (symlink into our lib dir), not a distro package.
_is_static_php(){ local l="/usr/sbin/php-fpm$1"; [ -L "$l" ] && readlink -f "$l" 2>/dev/null | grep -q "/usr/local/lib/bhserve/php/"; }

# ── apt helpers ──────────────────────────────────────────────────────────────
# Privileged runner (SUDO is set above: "" when already root via pkexec/sudo, else "sudo").
# Use `env` for the var: a bare "VAR=val cmd" after an EMPTY $SUDO makes bash try to RUN
# "VAR=val" as a command (the assignment prefix isn't re-recognised post-expansion).
# DPkg::Lock::Timeout=300 → wait up to 5 min for the dpkg lock (system apt-daily/unattended-
# upgrades or a previous BHServe install) instead of failing instantly with "Could not get lock".
_apt(){ $SUDO env DEBIAN_FRONTEND=noninteractive apt-get -o Dpkg::Use-Pty=0 -o DPkg::Lock::Timeout=300 "$@"; }
_APT_UPDATED=false
_apt_update_once(){ $_APT_UPDATED && return 0; _apt update >/dev/null 2>&1 || _apt update || true; _APT_UPDATED=true; }

_is_ubuntu(){ grep -qi ubuntu /etc/os-release 2>/dev/null; }
_codename(){ ( . /etc/os-release 2>/dev/null; echo "${VERSION_CODENAME:-stable}" ); }

# Ondřej Surý's repo provides php7.4–8.4 (+ -fpm) — the PPA on Ubuntu, deb.sury.org on Debian.
_ensure_php_repo(){
  if _is_ubuntu; then
    _php_repo_prepare
  else
    ls /etc/apt/sources.list.d/ 2>/dev/null | grep -qi sury && { _APT_UPDATED=false; return 0; }
    hdr "Adding the Sury PHP repository…"
    _apt install -y ca-certificates curl >/dev/null 2>&1 || true
    $SUDO install -d -m 0755 /usr/share/keyrings
    curl -fsSL https://packages.sury.org/php/apt.gpg | $SUDO tee /usr/share/keyrings/sury-php.gpg >/dev/null
    echo "deb [signed-by=/usr/share/keyrings/sury-php.gpg] https://packages.sury.org/php/ $(_codename) main" \
      | $SUDO tee /etc/apt/sources.list.d/sury-php.list >/dev/null
  fi
  _APT_UPDATED=false
}

# Ondřej's PPA gives multi-version PHP, but only for Ubuntu releases he's built for. A brand-new
# release (e.g. 26.04 'resolute') has NO build (404) AND his older builds need older libs that the
# new release replaced — so a codename-fallback produces unmet deps. Strategy: normalise the repo to
# THIS release; if it 404s, DISABLE it and fall back to Ubuntu's own PHP (built against this release's
# libs). The default PHP version Ubuntu ships then installs cleanly; older versions need Ondřej and
# will simply report unavailable until he builds for the release.
_php_repo_prepare(){
  local cur out f; cur="$(_codename)"
  if ! ls /etc/apt/sources.list.d/ 2>/dev/null | grep -qiE 'ondrej'; then
    hdr "Adding the Ondřej PHP repository (php7.4–8.4)…"
    _apt install -y software-properties-common ca-certificates >/dev/null 2>&1 || true
    $SUDO add-apt-repository -y ppa:ondrej/php >/dev/null 2>&1 || true
  fi
  shopt -s nullglob
  # re-enable any repo we disabled before + normalise its codename to this release (undo old rewrites)
  for f in /etc/apt/sources.list.d/*ondrej*php*.bhdisabled; do $SUDO mv "$f" "${f%.bhdisabled}" 2>/dev/null || true; done
  for f in /etc/apt/sources.list.d/*ondrej*php*.sources; do $SUDO sed -i -E "s/^([[:space:]]*Suites:).*/\1 $cur/I" "$f" 2>/dev/null || true; done
  for f in /etc/apt/sources.list.d/*ondrej*php*.list;    do $SUDO sed -i -E "s#(/ubuntu)[[:space:]]+[a-z]+[[:space:]]+main#\1 $cur main#" "$f" 2>/dev/null || true; done
  shopt -u nullglob
  out="$($SUDO apt-get update 2>&1 || true)"
  if echo "$out" | grep -qiE "ondrej.*(404|does not have a Release|no Release file)"; then
    warn "Ondřej PHP has no build for '$cur' yet — using Ubuntu's own PHP packages instead"
    shopt -s nullglob
    for f in /etc/apt/sources.list.d/*ondrej*php*.sources /etc/apt/sources.list.d/*ondrej*php*.list; do
      $SUDO mv "$f" "$f.bhdisabled" 2>/dev/null || true
    done
    shopt -u nullglob
    $SUDO apt-get update >/dev/null 2>&1 || true
  fi
}

# Common PHP extension set per version (mirrors the Mac/Windows default kit).
_php_pkgs(){
  local v="$1"
  echo "php$v-cli php$v-fpm php$v-common php$v-mysql php$v-pgsql php$v-sqlite3 php$v-curl php$v-mbstring php$v-xml php$v-zip php$v-gd php$v-intl php$v-bcmath php$v-soap php$v-readline php$v-opcache php$v-imagick php$v-gmp"
}

# Stop+disable the distro's own systemd unit for a service BHServe runs itself, so its
# auto-start can't grab :80/:443/:3306 out from under us.
_disable_system_unit(){ $SUDO systemctl disable --now "$1" >/dev/null 2>&1 || true; }

# ── install / update / uninstall  (apt, replacing the brew versions) ─────────
cmd_install() {
  need_init
  [ $# -ge 1 ] || die "usage: bhserve install <svc|all>  (e.g. nginx, php@8.4, mariadb, redis, mkcert, dnsmasq)"
  local targets=()
  if [ "$1" = all ]; then
    # MariaDB is the default DB; MySQL is an explicit alternative (they conflict), so skip it in `all`.
    while IFS='|' read -r key _ _ _; do [ -n "$key" ] && [ "$key" != mysql ] && targets+=("$key"); done < <(services)
  else targets=("$@"); fi
  _apt_update_once
  local key pkg v failed=0
  for key in "${targets[@]}"; do
    svc_exists "$key" || { warn "unknown service: $key (skipped)"; continue; }
    if svc_installed "$key"; then ok "$key already installed"; continue; fi
    pkg="$(svc_formula "$key")"
    case "$key" in
      php@*)
        v="${key#php@}"; _ensure_php_repo
        hdr "Installing $key"
        # Hybrid: prefer the DISTRO/Ondřej package (native, apt-managed extensions, ionCube-capable).
        # Core + WordPress-essential extensions are required; the rest are best-effort (one at a time)
        # so a package not built for this release doesn't fail the whole install.
        if _apt install -y "php$v-fpm" "php$v-cli" "php$v-common" "php$v-mysql" "php$v-curl" \
                           "php$v-mbstring" "php$v-xml" "php$v-zip" "php$v-gd" "php$v-intl" 2>/dev/null; then
          local _e
          for _e in pgsql sqlite3 bcmath soap readline opcache imagick gmp; do
            _apt install -y "php$v-$_e" >/dev/null 2>&1 || true
          done
          _disable_system_unit "php$v-fpm"; ok "$key installed (distro)"
        # Fallback: a fully-static portable build — works on any release the distro/Ondřej can't cover.
        elif _static_php_install "$v"; then
          : # _static_php_install already printed success
        else
          # Version-aware guidance. static-php-cli only builds 8.0+, so anything < 8.0 (7.4, EOL) can
          # ONLY come from Ondřej/Sury on a release they've packaged — never from the static fallback.
          if [ "${v%%.*}" -lt 8 ]; then
            no "PHP $v is end-of-life — it's only packaged on an LTS (Ubuntu 22.04/24.04 or Debian) via the Ondřej/Sury repo, and there's no static build for it. This release ($(_codename)) has no PHP $v. Use BHServe on an LTS for $v, or pick PHP 8.0+ here."
          else
            no "install $key failed — no distro package for $(_codename) and no portable build for PHP $v ($(_static_php_arch)). Static builds exist for 8.0–8.5; check the version."
          fi
          failed=1
        fi
        # Adopt a freshly-installed version as the default when the configured default isn't installed.
        # default_php may be stored bare (8.4) or prefixed (php@8.4, as `config set` writes it) — strip
        # any prefix so the check doesn't become svc_installed "php@php@8.4" (always false → hijacks default).
        if svc_installed "$key"; then
          local _dp; _dp="$(jget default_php 8.4)"; _dp="${_dp#php@}"
          svc_installed "php@$_dp" 2>/dev/null \
            || { json_set default_php "$v" 2>/dev/null && info "default PHP set to $v"; }
        fi ;;
      nginx)
        hdr "Installing nginx  (apt)"
        if _apt install -y nginx; then _disable_system_unit nginx; ok "nginx installed"; else no "install nginx failed"; failed=1; fi ;;
      httpd)
        hdr "Installing apache2  (apt)"
        if _apt install -y apache2 libapache2-mod-fcgid; then _disable_system_unit apache2; ok "apache2 installed"; else no "install apache2 failed"; failed=1; fi ;;
      mariadb|mysql)
        # mysql-server and mariadb-server conflict on Debian/Ubuntu — installing one makes apt REMOVE
        # the other. Warn so the swap (and any data migration in /var/lib/mysql) isn't a surprise.
        case "$key" in
          mysql)   svc_installed mariadb && warn "MariaDB is installed — installing MySQL will replace it (apt removes mariadb-server; data in /var/lib/mysql may need migration)." ;;
          mariadb) svc_installed mysql   && warn "MySQL is installed — installing MariaDB will replace it (apt removes mysql-server)." ;;
        esac
        hdr "Installing $pkg  (apt)"
        if _apt install -y "$pkg"; then
          $SUDO systemctl start "$key" >/dev/null 2>&1 || true
          _db_open_root "$key"
          $SUDO systemctl disable "$key" >/dev/null 2>&1 || true   # no autostart; BHServe starts it on demand
          ok "$key installed"
        else no "install $key failed"; failed=1; fi ;;
      fnm)
        hdr "Installing fnm (Node version manager)"
        _apt install -y unzip curl >/dev/null 2>&1 || true
        if curl -fsSL https://github.com/Schniz/fnm/releases/latest/download/fnm-linux.zip -o /tmp/bh-fnm.zip \
           && unzip -o /tmp/bh-fnm.zip -d /tmp/bh-fnm >/dev/null 2>&1 \
           && $SUDO install -m 0755 /tmp/bh-fnm/fnm /usr/local/bin/fnm; then
          rm -rf /tmp/bh-fnm.zip /tmp/bh-fnm; ok "fnm installed"
        else no "install fnm failed"; failed=1; fi ;;
      mailpit)
        hdr "Installing Mailpit"
        if curl -fsSL https://raw.githubusercontent.com/axllent/mailpit/develop/install.sh | $SUDO bash >/dev/null 2>&1 \
           && [ -x /usr/local/bin/mailpit ]; then ok "mailpit installed"; else no "install mailpit failed (download blocked?)"; failed=1; fi ;;
      postgresql@*)
        hdr "Installing $pkg  (apt)"
        if _apt install -y "$pkg"; then _disable_system_unit postgresql; ok "$key installed"; else no "install $key failed"; failed=1; fi ;;
      mkcert)
        hdr "Installing mkcert + NSS tools  (apt)"
        if _apt install -y mkcert libnss3-tools; then ok "mkcert installed"; else no "install mkcert failed"; failed=1; fi ;;
      *)
        hdr "Installing $key  ($pkg)"
        if _apt install -y "$pkg"; then ok "$key installed"; else no "install $key failed"; failed=1; fi ;;
    esac
    # Truth-check: even if apt returned 0, confirm the binary is actually present.
    case "$key" in fnm|mailpit) ;; *) svc_installed "$key" || { no "$key did not install (binary missing)"; failed=1; } ;; esac
  done
  return $failed
}

cmd_update() {
  need_init; [ $# -ge 1 ] || die "usage: bhserve update <svc|all>"
  _apt_update_once
  local targets=()
  if [ "$1" = all ]; then
    while IFS='|' read -r key _ _ _; do [ -n "$key" ] && svc_installed "$key" && targets+=("$key"); done < <(services)
  else targets=("$@"); fi
  local key role
  for key in "${targets[@]}"; do
    svc_exists "$key" || { warn "unknown service: $key"; continue; }
    svc_installed "$key" || { warn "$key not installed"; continue; }
    role="$(svc_role "$key")"
    hdr "Updating $key → latest  (apt upgrade)"
    case "$key" in
      php@*)
        if _is_static_php "${key#php@}"; then _static_php_install "${key#php@}"   # re-fetch newest static patch
        else _ensure_php_repo; _apt install -y --only-upgrade $(_php_pkgs "${key#php@}") 2>&1 | tail -2 || true; fi ;;
      *)     _apt install -y --only-upgrade "$(svc_formula "$key")" 2>&1 | tail -2 || true ;;
    esac
    # Restart BHServe's own running instance onto the new binary so sites keep working.
    case "$role" in
      php) fpm_running "$key" && { fpm_stop "$key" >/dev/null 2>&1; fpm_start "$key" >/dev/null 2>&1; } || true ;;
      web) [ "$key" = nginx ] && nginx_running && { nginx_stop >/dev/null 2>&1; nginx_start >/dev/null 2>&1; } || true ;;
      db|cache|mail) brew_svc_running "$key" && { brew_svc stop "$key" >/dev/null 2>&1; sleep 1; brew_svc start "$key" >/dev/null 2>&1; } || true ;;
    esac
    ok "$key updated"
  done
}

cmd_uninstall() {
  need_init; [ $# -ge 1 ] || die "usage: bhserve uninstall <svc>"
  local key pkg
  for key in "$@"; do
    svc_exists "$key" || { warn "unknown service: $key"; continue; }
    svc_installed "$key" || { warn "$key not installed"; continue; }
    # Portable static PHP: not an apt package — drop the symlink + the lib dir.
    if [[ "$key" == php@* ]] && _is_static_php "${key#php@}"; then
      hdr "Uninstalling $key  (portable build)"
      svc_stop_any "$key"
      $SUDO rm -f "/usr/sbin/php-fpm${key#php@}"
      $SUDO rm -rf "/usr/local/lib/bhserve/php/${key#php@}"
      ok "$key uninstalled"; continue
    fi
    pkg="$(svc_formula "$key")"
    hdr "Uninstalling $key  (apt remove $pkg)"
    svc_stop_any "$key"
    if _apt remove -y "$pkg"; then ok "$key uninstalled"; else no "uninstall $key failed"; fi
  done
}

# ── Service control for DB / cache / mail = systemd (the `brew services` analog) ──
# BHServe runs nginx + php-fpm itself; everything else is a distro daemon we toggle.
_systemd_unit(){
  case "$1" in
    mariadb)         echo mariadb ;;
    mysql)           echo mysql ;;
    postgresql@*)    echo postgresql ;;
    redis)           echo redis-server ;;
    memcached)       echo memcached ;;
    dnsmasq)         echo dnsmasq ;;
    *)               echo "$1" ;;
  esac
}
brew_svc(){ # action key
  local action="$1" key="$2" unit; unit="$(_systemd_unit "$key")"
  $SUDO systemctl "$action" "$unit" >/dev/null 2>&1 && ok "$key $action" || no "$key $action failed"
}

# cmd_api builds the db/cache/mail "running" flag from `brew services list` output (empty on
# Linux → everything looked stopped). Shim `brew services list` to emit one "<formula> <state>"
# line per service from systemd, so the api's existing awk detection works unchanged.
brew(){
  [ "${1:-}" = services ] && [ "${2:-}" = list ] || return 0
  local key formula _p role unit
  while IFS='|' read -r key formula _p role; do
    case "$role" in
      db|cache|mail)
        svc_installed "$key" || continue   # don't report state for a not-really-installed engine
        unit="$(_systemd_unit "$key")"
        if systemctl is-active --quiet "$unit" 2>/dev/null; then echo "$formula started"; else echo "$formula stopped"; fi ;;
    esac
  done < <(services)
}
# Called in places with either the service key or the brew formula; match on the unit.
brew_svc_running(){
  local arg="$1" unit
  case "$arg" in
    mariadb-server) unit=mariadb ;;
    mysql-server)   unit=mysql ;;
    postgresql-*)   unit=postgresql ;;
    redis-server)   unit=redis-server ;;
    *)              unit="$(_systemd_unit "$arg")" ;;
  esac
  systemctl is-active --quiet "$unit"
}

# ── doctor (Linux flavour: detect system daemons squatting on our ports) ─────
cmd_doctor() {
  hdr "BHServe doctor  (Linux / apt)"
  command -v apt-get >/dev/null && ok "apt: $(apt-get --version | head -1)" || no "apt-get not found"
  systemctl is-system-running >/dev/null 2>&1 && ok "systemd is running" || warn "systemd not the init system (some features need it)"
  hdr "Installed services"
  local key _f _p _r
  while IFS='|' read -r key _f _p _r; do
    [ -n "$key" ] || continue
    svc_installed "$key" && ok "$key  ($(probe_version "/$(svc_probe "$key")" 2>/dev/null || echo installed))" || true
  done < <(services)
  hdr "Port / daemon conflicts"
  local p
  for p in 80 443 3306 53; do
    if ss -ltnH "( sport = :$p )" 2>/dev/null | grep -q .; then
      info "something is listening on :$p"
    fi
  done
  systemctl is-enabled --quiet nginx 2>/dev/null   && warn "the distro 'nginx' unit is enabled — BHServe runs its own; run: sudo systemctl disable --now nginx"
  systemctl is-enabled --quiet apache2 2>/dev/null && warn "the distro 'apache2' unit is enabled — disable it: sudo systemctl disable --now apache2"
  systemctl is-active  --quiet systemd-resolved 2>/dev/null && info "systemd-resolved owns 127.0.0.53:53 — BHServe uses /etc/hosts for *.test by default (wildcard dnsmasq is opt-in)"
}

# ── DNS for *.test on Linux  (hosts-file default; wildcard is opt-in) ─────────
# Ubuntu owns 127.0.0.53:53 (systemd-resolved), so the robust, daemonless default is a
# managed block in /etc/hosts — one line per BHServe domain, rewritten from the live
# vhosts on every change. We override maybe_reload_nginx (called by EVERY add/rm/secure/
# node/py path) so /etc/hosts stays in sync with no edits to the shared engine.
HOSTS_BEGIN="# BHSERVE-START (managed — do not edit; rewritten on site changes)"
HOSTS_END="# BHSERVE-END"

# All domains currently served (server_name lines across every vhost, minus the catch-all).
_bh_all_domains(){
  grep -hoE 'server_name[[:space:]]+[^;]+;' "$BH_HOME"/nginx/sites/*.conf 2>/dev/null \
    | sed -E 's/server_name[[:space:]]+//; s/;//' \
    | tr ' ' '\n' | grep -vE '^(_|)$' | sort -u
}

# Rewrite the managed block in /etc/hosts to exactly the current domain set (idempotent).
hosts_sync_all(){
  local want block line
  want="$(_bh_all_domains)"
  block="$HOSTS_BEGIN"$'\n'
  while IFS= read -r line; do [ -n "$line" ] && block+="127.0.0.1 $line"$'\n'; done <<<"$want"
  block+="$HOSTS_END"
  # Current managed block (if any) — skip the rewrite when nothing changed (no sudo prompt).
  local cur=""
  if grep -qF "$HOSTS_BEGIN" /etc/hosts 2>/dev/null; then
    cur="$(awk -v b="$HOSTS_BEGIN" -v e="$HOSTS_END" '$0==b{f=1} f{print} $0==e{f=0}' /etc/hosts)"
  fi
  [ "$cur" = "$block" ] && return 0
  local tmp; tmp="$(mktemp)"
  # everything OUTSIDE the managed block, then append the fresh block
  awk -v b="$HOSTS_BEGIN" -v e="$HOSTS_END" '$0==b{s=1} !s{print} $0==e{s=0}' /etc/hosts > "$tmp" 2>/dev/null || true
  # SAFETY: never clobber /etc/hosts with just our block — the remainder must still carry a
  # loopback entry (it always does on a real system). If not, the read failed → abort.
  if ! grep -qiE '^[[:space:]]*(127\.0\.0\.1|::1)[[:space:]]' "$tmp"; then
    warn "skipped /etc/hosts sync — existing file looks unreadable/empty (left untouched)"
    rm -f "$tmp"; return 0
  fi
  # drop trailing blank lines, then the block
  sed -e :a -e '/^\n*$/{$d;N;ba}' "$tmp" > "$tmp.2" 2>/dev/null && mv "$tmp.2" "$tmp"
  printf '%s\n%s\n' "$(cat "$tmp")" "$block" | $SUDO tee /etc/hosts >/dev/null && \
    info "synced /etc/hosts ($(printf '%s' "$want" | grep -c . ) domain(s))"
  rm -f "$tmp" "$tmp.2" 2>/dev/null || true
}

# Override: keep /etc/hosts in lock-step, then do the normal reload. Regular site_add/rm
# go through here; in non-tty (GUI) mode it still syncs hosts before deferring the reload.
maybe_reload_nginx(){
  hosts_sync_all
  nginx_running || return 0
  if [ -t 1 ]; then nginx_reload; else info "reload nginx to serve changes (bhserve restart nginx)"; fi
}

# nodesite/pysite add + `secure` call nginx_reload DIRECTLY (not via maybe_reload_nginx),
# so sync /etc/hosts here too — this is the real choke point every reload passes through.
# hosts_sync_all is idempotent (no-op + no sudo when nothing changed), so double calls are free.
nginx_reload(){
  hosts_sync_all
  nginx_running || return 0
  local bin pre=""; bin="$(NGINX_BIN)"; needs_root_ports && pre="sudo"
  $pre "$bin" -s reload -c "$BH_HOME/nginx/nginx.conf" -p "$BH_HOME/nginx" 2>/dev/null \
    && ok "nginx reloaded" || warn "reload failed — run: bhserve restart nginx"
}

# `bhserve dns` — default: (re)sync /etc/hosts for all sites. `bhserve dns wildcard` —
# opt-in true *.test via a dnsmasq + systemd-resolved drop-in (experimental on desktops).
cmd_dns() {
  need_init
  local mode="${1:-hosts}"
  case "$mode" in
    hosts|"")
      hdr "DNS: /etc/hosts mode (default)"
      hosts_sync_all
      ok "all BHServe domains point to 127.0.0.1 via /etc/hosts"
      info "for true wildcard *.$(jget tld test): bhserve dns wildcard" ;;
    wildcard)
      hdr "DNS: wildcard *.$(jget tld test) via dnsmasq + systemd-resolved"
      svc_installed dnsmasq || die "dnsmasq not installed — run: bhserve install dnsmasq"
      local tld; tld="$(jget tld test)"
      # dnsmasq on a private loopback (avoids resolved's 127.0.0.53:53) answering *.tld.
      $SUDO mkdir -p /etc/dnsmasq.d
      printf 'no-resolv\nlisten-address=127.0.0.54\nbind-interfaces\naddress=/%s/127.0.0.1\n' "$tld" \
        | $SUDO tee /etc/dnsmasq.d/bhserve.conf >/dev/null
      $SUDO systemctl restart dnsmasq 2>/dev/null || $SUDO systemctl start dnsmasq 2>/dev/null || true
      # route .tld lookups to that dnsmasq via a resolved drop-in
      $SUDO mkdir -p /etc/systemd/resolved.conf.d
      printf '[Resolve]\nDNS=127.0.0.54\nDomains=~%s\n' "$tld" \
        | $SUDO tee /etc/systemd/resolved.conf.d/bhserve.conf >/dev/null
      $SUDO systemctl restart systemd-resolved 2>/dev/null || true
      ok "wildcard *.$tld → 127.0.0.1 (dnsmasq on 127.0.0.54, routed via systemd-resolved)"
      info "test:  getent hosts anything.$tld" ;;
    *) die "usage: bhserve dns [hosts|wildcard]" ;;
  esac
}

# ── Start-at-login = a systemd --user service (the LaunchAgent analog) ────────
# Runs `bhserve start all` at login so sites are up before/at session start. The api's
# loginitem flag maps to `systemctl --user is-enabled`.
_loginitem_unit(){ echo "$HOME/.config/systemd/user/bhserve.service"; }
loginitem_enabled(){ systemctl --user is-enabled bhserve.service >/dev/null 2>&1; }
cmd_loginitem(){
  local sub="${1:-status}" unit; unit="$(_loginitem_unit)"
  local engine="${_bh_engine_dir:-$(dirname "$(readlink -f "$0")")}/bhserve"
  case "$sub" in
    enable)
      mkdir -p "$(dirname "$unit")"
      cat > "$unit" <<UNIT
[Unit]
Description=BHServe — start local web services at login
After=network.target

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/bin/bash $engine start all
ExecStop=/bin/bash $engine stop all

[Install]
WantedBy=default.target
UNIT
      systemctl --user daemon-reload >/dev/null 2>&1 || true
      systemctl --user enable bhserve.service >/dev/null 2>&1 && ok "start-at-login enabled (systemd --user)" || warn "couldn't enable the user service"
      loginctl enable-linger "$USER_NAME" >/dev/null 2>&1 || true   # allow pre-login start
      ;;
    disable)
      systemctl --user disable bhserve.service >/dev/null 2>&1 || true
      rm -f "$unit"; systemctl --user daemon-reload >/dev/null 2>&1 || true
      ok "start-at-login disabled" ;;
    status) loginitem_enabled && echo enabled || echo disabled ;;
    *) die "usage: bhserve loginitem {enable|disable|status}" ;;
  esac
}

# ── Privileged helper (Linux): a SCOPED sudoers rule for the nginx binary ONLY ──
# The macOS helper_install is brew/BSD-specific (wrong nginx path, `stat -f`, `-g wheel`).
# On Linux we whitelist exactly /usr/sbin/nginx for the desktop user so the systemd --user
# autostart can bind :80/:443 unattended. Nothing else is made passwordless — installs,
# systemctl, mkcert, /etc/hosts writes all still go through a per-action polkit prompt.
helper_installed(){ [ -f "$SUDOERS_FILE" ]; }
helper_install(){
  [ "$(id -u)" = 0 ] || exec pkexec "$(readlink -f "$0")" helper install
  local u tmp; u="${USER_NAME:-${SUDO_USER:-}}"
  [ -n "$u" ] && [ "$u" != root ] || die "could not determine the desktop user"
  tmp="$(mktemp)"
  cat > "$tmp" <<SUDO
# BHServe — let $u start/stop/reload nginx (binds :80/:443) without a password.
# Remove with: sudo bhserve helper uninstall
Defaults:$u !requiretty
$u ALL=(root) NOPASSWD: /usr/sbin/nginx
SUDO
  visudo -cf "$tmp" >/dev/null 2>&1 || { rm -f "$tmp"; die "sudoers validation failed — not installed"; }
  install -m 0440 -o root -g root "$tmp" "$SUDOERS_FILE"; rm -f "$tmp"
  ok "password-less nginx control enabled (autostart can bind :80/:443 unattended)"
}
helper_uninstall(){
  [ "$(id -u)" = 0 ] || exec pkexec "$(readlink -f "$0")" helper uninstall
  rm -f "$SUDOERS_FILE" && ok "privileged helper removed" || die "remove failed"
}

# ── Databases (Ubuntu client / auth / path deltas) ───────────────────────────
MYSQL_CLI(){ local c; for c in /usr/bin/mariadb /usr/bin/mysql; do [ -x "$c" ] && { echo "$c"; return; }; done; echo /usr/bin/mariadb; }
PSQL_CLI(){ echo /usr/bin/psql; }
PG_BIN(){ local v; for v in 17 16 15; do [ -x "/usr/lib/postgresql/$v/bin/$1" ] && { echo "/usr/lib/postgresql/$v/bin/$1"; return; }; done; echo "/usr/lib/postgresql/16/bin/$1"; }

# Ubuntu MariaDB root@localhost defaults to unix_socket auth → only the OS root user
# connects password-less. Try the user's own socket, then `sudo <client>` (root socket),
# then -u root, so db verbs work however the server is configured.
mysql_run(){
  local c; c="$(MYSQL_CLI)"; [ -x "$c" ] || return 127
  if "$c" -N -e "SELECT 1;" >/dev/null 2>&1; then "$c" "$@"
  elif $SUDO "$c" -N -e "SELECT 1;" >/dev/null 2>&1; then $SUDO "$c" "$@"
  elif "$c" -u root -N -e "SELECT 1;" >/dev/null 2>&1; then "$c" -u root "$@"
  else return 1; fi
}

# Make root@localhost a BLANK-password native account — same posture as the Mac (root has
# no password; the server is bound to loopback only) so the engine + WordPress connect over
# TCP as root. Tries MariaDB then MySQL syntax.
_db_open_root(){
  local cli i; cli="$(MYSQL_CLI)"
  for i in 1 2 3 4 5 6; do $SUDO "$cli" -e "SELECT 1" >/dev/null 2>&1 && break; sleep 1; done
  $SUDO "$cli" -e "ALTER USER 'root'@'localhost' IDENTIFIED VIA mysql_native_password USING ''; FLUSH PRIVILEGES;" >/dev/null 2>&1 \
    || $SUDO "$cli" -e "ALTER USER 'root'@'localhost' IDENTIFIED WITH mysql_native_password BY ''; FLUSH PRIVILEGES;" >/dev/null 2>&1 \
    || $SUDO "$cli" -e "SET PASSWORD FOR 'root'@'localhost' = ''; FLUSH PRIVILEGES;" >/dev/null 2>&1 || true
  db_secure_bind
}

# The macOS db_ready_rootblank waits on /tmp/mysql.sock (Homebrew). Ubuntu's MariaDB socket is
# /run/mysqld/mysqld.sock, so that check always failed → WordPress/PHP sites silently skipped DB
# provisioning. Verify readiness via mysql_run (works whatever the socket path is).
db_ready_rootblank(){
  svc_installed mariadb || svc_installed mysql || return 1
  brew_svc_running mariadb || brew_svc start mariadb >/dev/null 2>&1 || brew_svc start mysql >/dev/null 2>&1 || true
  local i
  for i in $(seq 1 15); do mysql_run -e "SELECT 1" >/dev/null 2>&1 && return 0; sleep 1; done
  return 1
}

# Ubuntu MariaDB already binds 127.0.0.1 by default; write an idempotent drop-in in the
# distro's conf.d (path differs from brew's etc/my.cnf.d) to guarantee it.
db_secure_bind(){
  local d f
  if   [ -d /etc/mysql/mariadb.conf.d ]; then d=/etc/mysql/mariadb.conf.d
  elif [ -d /etc/mysql/mysql.conf.d ];   then d=/etc/mysql/mysql.conf.d
  else return 0; fi
  f="$d/99-bhserve.cnf"
  $SUDO grep -q '127\.0\.0\.1' "$f" 2>/dev/null && return 0
  printf '# BHServe — keep the DB on localhost only (root has no password)\n[mysqld]\nbind-address = 127.0.0.1\n' \
    | $SUDO tee "$f" >/dev/null 2>&1 || true
}

# ── self-update (CLI convenience) ─────────────────────────────────────────────
# Fetch + install the newest BHServe .deb straight from GitHub releases, so terminal users don't have
# to track version numbers or chase download URLs. (The desktop app has its own in-app updater.)
cmd_self_update(){
  local api url latest cur tmp
  # `|| true` on every command substitution: the engine runs under `set -e`, where a failing curl/
  # dpkg-query in `var="$(…)"` would abort the function before our own error handling runs.
  cur="$(dpkg-query -W -f='${Version}' bhserve 2>/dev/null || true)"
  hdr "Checking for the latest BHServe release…"
  api="$(curl -fsSL "https://api.github.com/repos/wpexpertinbd/BHServe/releases?per_page=20" 2>/dev/null || true)"
  [ -n "$api" ] || { no "couldn't reach GitHub (offline, or API rate-limited — retry shortly)"; return 1; }
  case "$api" in *'rate limit'*) no "GitHub API rate limit hit — wait ~1 min and retry (or grab the .deb from the releases page)"; return 1 ;; esac
  url="$(printf '%s\n' "$api" | grep -oE 'https://github.com/[^"]*bhserve_[0-9.]+_all\.deb' | head -1 || true)"
  [ -n "$url" ] || { no "no Linux .deb asset found in the latest releases"; return 1; }
  latest="$(printf '%s' "$url" | sed -E 's#.*bhserve_([0-9.]+)_all\.deb#\1#' || true)"
  info "installed: ${cur:-unknown}    latest: $latest"
  if [ -n "$cur" ] && [ "$cur" = "$latest" ]; then ok "already up to date ($cur)"; return 0; fi
  tmp="$(mktemp -d)"
  hdr "Downloading + installing $latest…"
  if curl -fsSL "$url" -o "$tmp/bhserve.deb" \
     && $SUDO env DEBIAN_FRONTEND=noninteractive apt-get install -y --allow-downgrades "$tmp/bhserve.deb"; then
    rm -rf "$tmp"; ok "updated ${cur:-?} → $latest — restart the BHServe app if it's open"; return 0
  fi
  rm -rf "$tmp"; no "self-update failed (download or apt error)"; return 1
}

# Linux-only verb, not in the shared dispatch: intercept it here (platform-linux.sh is sourced just
# before the shared `case`), so the shared engine + macOS build stay untouched.
case "${1:-}" in
  self-update|self_update|selfupdate) cmd_self_update "${@:2}"; exit $? ;;
esac
