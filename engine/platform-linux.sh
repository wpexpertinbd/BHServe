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
  # BHSERVE_OWNER_UID: set by the boot-time system unit (loginitem), where there is no
  # PKEXEC_UID/SUDO_UID — without it a boot `start all` would resolve USER_NAME=root and
  # render nginx/php-fpm workers as ROOT. Interactive pkexec/sudo still win the fallback chain.
  _ru="${PKEXEC_UID:-${SUDO_UID:-${BHSERVE_OWNER_UID:-}}}"
  if [ -n "${_ru:-}" ] && [ "$_ru" != 0 ]; then
    USER_NAME="$(getent passwd "$_ru" | cut -d: -f1)"
    GROUP_NAME="$(id -gn "$USER_NAME" 2>/dev/null || echo "$USER_NAME")"
    export HOME="$(getent passwd "$_ru" | cut -d: -f6)"
    BH_HOME="${BHSERVE_HOME:-$HOME/.bhserve}"
  fi
  SUDO=""   # already root — no nested elevation
else
  SUDO="${BHSERVE_SUDO:-sudo}"
  # From the GUI there is NO controlling terminal, so a privileged op that needs a password
  # would BLOCK on an invisible sudo/polkit prompt → the app hangs ("BHServe Is Not
  # Responding"). This is exactly what an unprivileged OLS reload (`sudo systemctl restart
  # lsws`, no NOPASSWD rule) did. Use NON-INTERACTIVE sudo in GUI context only: NOPASSWD-
  # allowed ops still run; everything else fails fast (such calls are best-effort `|| true`),
  # so nothing can hang. Privileged verbs run via pkexec as root (SUDO="" above) and are
  # unaffected. Deliberately NOT gated on `! -t 1`: sudo prompts on /dev/tty (not stdout), so
  # a terminal user piping output (`bhserve install X | tee log`) must keep the normal prompt.
  if [ "${BHSERVE_GUI:-}" = 1 ]; then SUDO="$SUDO -n"; fi
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
openlitespeed|openlitespeed|usr/local/lsws/bin/lswsctrl|web
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
#
# Use the 'bulk' preset, NOT 'common': the 'common' build ships pdo_mysql + mysqlnd but NOT the
# mysqli extension, and mysqli is REQUIRED by both WordPress and phpMyAdmin — i.e. the default
# stack. 'bulk' includes mysqli (verified) so the default PHP works standalone. (~29MB vs 11MB.)
_STATIC_PHP_BASE="https://dl.static-php.dev/static-php-cli/bulk"
# HTTPS-only curl flags (incl. redirects) for every download that lands a root-installed/-executed
# artifact — defence against a protocol-downgrade or http redirect MITM. Used unquoted on purpose so
# the two tokens word-split into separate args.
_CURL_HTTPS='--proto =https --proto-redir =https'
_static_php_arch(){ case "$(uname -m)" in aarch64|arm64) echo aarch64 ;; *) echo x86_64 ;; esac; }
_static_php_install(){
  local v="$1" arch file tmp bin sysdir
  # Guard: v flows into "$SUDO ln -sf …/usr/sbin/php-fpm$v" and (in the heal path) "$SUDO rm" —
  # never let anything but a bare major.minor near a privileged path.
  [[ "$v" =~ ^[0-9]+\.[0-9]+$ ]] || { no "invalid PHP version '$v'"; return 1; }
  arch="$(_static_php_arch)"
  # Never clobber a real distro php-fpm binary — if one is already at the standard path (and it's
  # not our own symlink), the distro provides this version; nothing to do.
  if [ -e "/usr/sbin/php-fpm$v" ] && ! _is_static_php "$v"; then
    ok "PHP $v already provided by the distro package"; return 0
  fi
  hdr "Installing PHP $v  (portable static build)"
  file="$(curl $_CURL_HTTPS -fsSL "$_STATIC_PHP_BASE/" 2>/dev/null \
          | grep -oE "php-$v\.[0-9]+-fpm-linux-$arch\.tar\.gz" | sort -uV | tail -1)"
  [ -n "$file" ] || { no "no portable PHP $v build available for $arch"; return 1; }
  sysdir="/usr/local/lib/bhserve/php/$v"; tmp="$(mktemp -d)"
  # --no-same-owner: don't honour uid/gid baked into the archive; -C an isolated mktemp dir, then
  # install ONLY the php-fpm we find (a hostile member elsewhere in the tree is never used).
  if curl $_CURL_HTTPS -fsSL "$_STATIC_PHP_BASE/$file" -o "$tmp/fpm.tgz" \
     && tar --no-same-owner -xzf "$tmp/fpm.tgz" -C "$tmp" 2>/dev/null \
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
    curl $_CURL_HTTPS -fsSL https://packages.sury.org/php/apt.gpg | $SUDO tee /usr/share/keyrings/sury-php.gpg >/dev/null
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
      openlitespeed)
        if ols_install; then ok "openlitespeed installed"; else no "install openlitespeed failed"; failed=1; fi ;;
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
        # mktemp (not a predictable /tmp/bh-fnm) — avoids a symlink/TOCTOU race on the shared /tmp.
        local _ft; _ft="$(mktemp -d)"
        if curl $_CURL_HTTPS -fsSL https://github.com/Schniz/fnm/releases/latest/download/fnm-linux.zip -o "$_ft/fnm.zip" \
           && unzip -o "$_ft/fnm.zip" -d "$_ft" >/dev/null 2>&1 \
           && $SUDO install -m 0755 "$_ft/fnm" /usr/local/bin/fnm; then
          rm -rf "$_ft"; ok "fnm installed"
        else rm -rf "$_ft"; no "install fnm failed"; failed=1; fi ;;
      mailpit)
        hdr "Installing Mailpit"
        if curl $_CURL_HTTPS -fsSL https://raw.githubusercontent.com/axllent/mailpit/develop/install.sh | $SUDO bash >/dev/null 2>&1 \
           && [ -x /usr/local/bin/mailpit ]; then ok "mailpit installed"; _mailpit_unit; else no "install mailpit failed (download blocked?)"; failed=1; fi ;;
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
      web) [ "$key" = nginx ] && nginx_running && nginx_restart >/dev/null 2>&1 || true ;;
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

# Mailpit ships no systemd unit — its install script only drops the binary. Write a system
# unit so start/stop/enable + the api status shim all work via systemctl like every other
# daemon. Binds loopback only: SMTP 127.0.0.1:1025, web UI 127.0.0.1:8025 (nginx proxies it).
_mailpit_unit(){
  local unit=/etc/systemd/system/mailpit.service
  $SUDO tee "$unit" >/dev/null <<'UNIT'
[Unit]
Description=Mailpit (BHServe mail catcher)
After=network.target

[Service]
Type=simple
ExecStart=/usr/local/bin/mailpit --smtp 127.0.0.1:1025 --listen 127.0.0.1:8025
Restart=on-failure
DynamicUser=yes

[Install]
WantedBy=multi-user.target
UNIT
  $SUDO systemctl daemon-reload >/dev/null 2>&1 || true
  ok "mailpit systemd unit installed"
}
# Called by the shared mailpit_setup — create the unit if an earlier install predates it.
mailpit_platform_setup(){ [ -f /etc/systemd/system/mailpit.service ] || _mailpit_unit; }

# Distro MariaDB/MySQL socket (Debian/Ubuntu) — used as the fall-back when the server can't be
# queried and no socket file is present yet (the shared default /tmp/mysql.sock is macOS-only).
db_default_socket(){ echo /run/mysqld/mysqld.sock; }

# cloudflared (Cloudflare quick tunnels). Installed under the USER-owned config root (~/.bhserve/bin),
# NOT /usr/local/bin — because `tunnel` is not a privileged verb (the GUI runs it without pkexec), so
# the auto-install on first share must not need sudo. cloudflared_installed() (shared) -x's this path.
cloudflared_bin(){ echo "$BH_HOME/bin/cloudflared"; }

# Install cloudflared from Cloudflare's official static binary (no apt repo/account/sudo needed).
# Called by the shared cmd_tunnel/tunnel_start, so the GUI "Share publicly" auto-installs on first use.
cloudflared_install(){
  local arch url bin tmp
  case "$(uname -m)" in aarch64|arm64) arch=arm64 ;; armv7*|armhf|arm) arch=arm ;; *) arch=amd64 ;; esac
  url="https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-linux-$arch"
  bin="$(cloudflared_bin)"; tmp="$(mktemp)"
  hdr "Installing cloudflared  (linux-$arch)"
  mkdir -p "$(dirname "$bin")"
  if curl $_CURL_HTTPS -fsSL "$url" -o "$tmp" && [ -s "$tmp" ]; then
    install -m 0755 "$tmp" "$bin"; rm -f "$tmp"   # ~/.bhserve is user-owned → no sudo
    cloudflared_installed && { ok "cloudflared installed ($bin)"; return 0; } || { no "cloudflared install failed"; return 1; }
  fi
  rm -f "$tmp"; no "cloudflared download failed (network blocked?)"; return 1
}

# php-fpm<v> binary path for a version (portable symlink or distro), else its cli, else "".
_php_bin_for(){
  local v="$1" b="/usr/sbin/php-fpm$v"
  [ -x "$b" ] && { echo "$b"; return; }
  command -v "php$v" 2>/dev/null
}
# True when the given PHP version exposes the mysqli extension.
_php_has_mysqli(){ local b; b="$(_php_bin_for "$1")"; [ -n "$b" ] && "$b" -m 2>/dev/null | grep -qi '^mysqli$'; }

# Ensure the DEFAULT/requested PHP itself has mysqli (required by WordPress + phpMyAdmin). We heal
# the SAME version in place — never fall back to a different php the user may not have installed;
# the default stack must work standalone. Distro PHP → apt php<v>-mysql; portable static PHP →
# refetch (the source is the 'bulk' preset, which includes mysqli). All messages go to stderr;
# only the php key is printed on stdout.
db_ext_ensure(){
  local want="${1:-default}"
  case "$want" in default|"") want="$(jget default_php 8.4)";; esac
  want="${want#php@}"
  # $want reaches "$SUDO rm /usr/sbin/php-fpm$want" below — accept only a bare major.minor.
  [[ "$want" =~ ^[0-9]+\.[0-9]+$ ]] || { printf 'php@%s\n' "$want"; return 0; }
  if _php_has_mysqli "$want"; then printf 'php@%s\n' "$want"; return 0; fi
  {
    if _is_static_php "$want"; then
      # A static 'common' build lacks mysqli and can't load it as a .so — refetch the full
      # (bulk) build for this version, which has it compiled in. Force a re-download by clearing
      # our symlink first (else _static_php_install may short-circuit).
      hdr "PHP $want is missing mysqli — refetching a fuller portable build"
      $SUDO rm -f "/usr/sbin/php-fpm$want" "/usr/local/lib/bhserve/php/$want/php-fpm" 2>/dev/null || true
      _static_php_install "$want" || warn "refetch failed — could not add mysqli to PHP $want"
    else
      hdr "Adding the MySQL PHP extension (php$want-mysql)"
      _apt_update_once
      if _apt install -y "php$want-mysql"; then
        $SUDO phpenmod -v "$want" mysqli pdo_mysql >/dev/null 2>&1 || true
        ok "php$want-mysql installed (mysqli + pdo_mysql)"
      else warn "could not install php$want-mysql — phpMyAdmin needs mysqli"; fi
    fi
    fpm_running "php@$want" && { fpm_stop "php@$want" >/dev/null 2>&1; fpm_start "php@$want" >/dev/null 2>&1; } || true
  } >&2
  printf 'php@%s\n' "$want"
}

# Resolve a brew formula OR a BHServe service key to its systemd unit name.
_unit_for(){
  case "$1" in
    mariadb|mariadb-server)  echo mariadb ;;
    mysql|mysql-server)      echo mysql ;;
    postgresql*|postgres)    echo postgresql ;;
    redis|redis-server)      echo redis-server ;;
    memcached)               echo memcached ;;
    dnsmasq)                 echo dnsmasq ;;
    mailpit)                 echo mailpit ;;
    *)                       echo "$1" ;;
  esac
}

# cmd_api builds the db/cache/mail "running" flag from `brew services list` output (empty on
# Linux → everything looked stopped). Also, shared code calls `brew services start|stop <formula>`
# directly (e.g. mailpit_setup) — on Linux route those to systemctl. Shim `brew services …`:
brew(){
  [ "${1:-}" = services ] || return 0
  local sub="${2:-}"
  case "$sub" in
    list)
      local key formula _p role unit
      while IFS='|' read -r key formula _p role; do
        case "$role" in
          db|cache|mail)
            svc_installed "$key" || continue   # don't report state for a not-really-installed engine
            unit="$(_systemd_unit "$key")"
            if systemctl is-active --quiet "$unit" 2>/dev/null; then echo "$formula started"; else echo "$formula stopped"; fi ;;
        esac
      done < <(services) ;;
    start|stop|restart)
      local unit; unit="$(_unit_for "${3:-}")"
      $SUDO systemctl "$sub" "$unit" >/dev/null 2>&1 || true ;;
  esac
  return 0
}
# dnsmasq status on Linux. BHServe-Linux resolves *.test via a managed /etc/hosts block by
# default (systemd-resolved owns 127.0.0.53:53), so dnsmasq is an OPTIONAL wildcard enhancement,
# not a required daemon — Ubuntu even ships dnsmasq-base with no running unit. Treat it like
# mkcert/fnm: "active" once installed, but still report the live daemon if one IS running.
dnsmasq_running(){
  pgrep -x dnsmasq >/dev/null 2>&1 && return 0
  systemctl is-active --quiet dnsmasq 2>/dev/null && return 0
  svc_installed dnsmasq
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
  # ALWAYS (re)load nginx when a site changes. The old `[ -t 1 ]` tty gate meant the GUI (which runs
  # us non-interactively) never actually reloaded — so a GUI-added site's vhost wasn't served until a
  # manual restart. And if nginx is down, START it (else the new site 502s / doesn't load at all).
  if nginx_running; then nginx_reload; else nginx_start >/dev/null 2>&1 && ok "nginx started" || warn "start nginx to serve the site (bhserve start nginx)"; fi
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

# ── Start-at-login = a SYSTEM systemd unit + a per-user tray autostart ────────
# The services need ROOT to start (nginx/apache on :80/:443, systemctl for the DBs), so a
# `systemctl --user` unit (≤1.0.49) could never actually start them at login — it just hit an
# invisible password prompt and did nothing. The working design (parity with the macOS launch
# daemon / Windows service): a SYSTEM unit runs `start all` as root at BOOT (workers still drop
# to the desktop user via BHSERVE_OWNER_UID), and a user autostart .desktop shows the tray at
# login. This verb runs PRIVILEGED (pkexec) — and there's no enable/is-enabled mismatch: enable
# (root) and the api's unprivileged `systemctl is-enabled` both read the same SYSTEM state.
_loginitem_unit(){ echo /etc/systemd/system/bhserve.service; }
_loginitem_autostart(){ echo "$HOME/.config/autostart/com.biswashost.bhserve-tray.desktop"; }
loginitem_enabled(){ systemctl is-enabled bhserve.service >/dev/null 2>&1; }
# Retire the ≤1.0.49 per-user unit so it can't double-run `start all`. Bus-free removal (unit
# file + WantedBy symlink) always works; the user-bus disable is best-effort on top.
_loginitem_retire_user_unit(){
  if [ "$(id -u)" = 0 ] && [ -n "${USER_NAME:-}" ] && [ "$USER_NAME" != root ]; then
    XDG_RUNTIME_DIR="/run/user/$(id -u "$USER_NAME")" runuser -u "$USER_NAME" -- \
      systemctl --user disable bhserve.service >/dev/null 2>&1 || true
  else
    systemctl --user disable bhserve.service >/dev/null 2>&1 || true
  fi
  rm -f "$HOME/.config/systemd/user/bhserve.service" \
        "$HOME/.config/systemd/user/default.target.wants/bhserve.service" 2>/dev/null || true
  loginctl disable-linger "${USER_NAME:-$(id -un)}" >/dev/null 2>&1 || true
}
cmd_loginitem(){
  local sub="${1:-status}" unit auto; unit="$(_loginitem_unit)"; auto="$(_loginitem_autostart)"
  local engine="${_bh_engine_dir:-$(dirname "$(readlink -f "$0")")}/bhserve"
  case "$sub" in
    enable)
      [ "$(id -u)" = 0 ] || die "start-at-login needs admin — run: sudo bhserve loginitem enable"
      _loginitem_retire_user_unit
      # bake the desktop user's uid into the unit: at boot there is no PKEXEC_UID/SUDO_UID,
      # so the engine reads BHSERVE_OWNER_UID to run workers/sockets as the user (not root).
      local owner_uid; owner_uid="${_ru:-$(id -u "${USER_NAME:-$(id -un)}")}"
      cat > "$unit" <<UNIT
[Unit]
Description=BHServe — start local web services at boot
After=network.target
Wants=network.target

[Service]
Type=oneshot
RemainAfterExit=yes
Environment=BHSERVE_OWNER_UID=$owner_uid
Environment=BHSERVE_HOME=$BH_HOME
ExecStart=/bin/bash $engine start all
ExecStop=/bin/bash $engine stop all

[Install]
WantedBy=multi-user.target
UNIT
      systemctl daemon-reload >/dev/null 2>&1 || true
      systemctl enable bhserve.service >/dev/null 2>&1 && ok "start-at-boot enabled (system service)" || warn "couldn't enable the system service"
      # tray at login: per-user autostart entry launching the GUI hidden-to-tray.
      mkdir -p "$(dirname "$auto")"
      cat > "$auto" <<DESK
[Desktop Entry]
Type=Application
Name=BHServe (tray)
Comment=Show the BHServe tray icon at login
Exec=bhserve-gui --background
Icon=com.biswashost.bhserve
Terminal=false
X-GNOME-Autostart-enabled=true
Categories=Development;
DESK
      # written by root (pkexec) into the USER's config — hand it back to them.
      if [ -n "${USER_NAME:-}" ] && [ "$USER_NAME" != root ]; then
        chown "$USER_NAME":"$GROUP_NAME" "$auto" "$(dirname "$auto")" 2>/dev/null || true
      fi
      ok "tray will appear at login"
      ;;
    disable)
      [ "$(id -u)" = 0 ] || die "start-at-login needs admin — run: sudo bhserve loginitem disable"
      # NOT `disable --now`: --now would run ExecStop (`stop all`) and kill the user's
      # RUNNING services just for turning the login toggle off.
      systemctl disable bhserve.service >/dev/null 2>&1 || true
      rm -f "$unit"; systemctl daemon-reload >/dev/null 2>&1 || true
      rm -f "$auto" 2>/dev/null || true
      _loginitem_retire_user_unit
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
  api="$(curl $_CURL_HTTPS -fsSL "https://api.github.com/repos/wpexpertinbd/BHServe/releases?per_page=20" 2>/dev/null || true)"
  [ -n "$api" ] || { no "couldn't reach GitHub (offline, or API rate-limited — retry shortly)"; return 1; }
  case "$api" in *'rate limit'*) no "GitHub API rate limit hit — wait ~1 min and retry (or grab the .deb from the releases page)"; return 1 ;; esac
  # Pin the host: escaped dots (github\.com, not github<any>com) so the asset URL can't be spoofed to
  # a look-alike host in a tampered API body.
  url="$(printf '%s\n' "$api" | grep -oE 'https://github\.com/[^"]*bhserve_[0-9.]+_all\.deb' | head -1 || true)"
  [ -n "$url" ] || { no "no Linux .deb asset found in the latest releases"; return 1; }
  # Belt-and-suspenders host allowlist before we hand the URL to curl.
  case "$url" in https://github.com/*) ;; *) no "refusing non-GitHub download URL"; return 1 ;; esac
  latest="$(printf '%s' "$url" | sed -E 's#.*bhserve_([0-9.]+)_all\.deb#\1#' || true)"
  info "installed: ${cur:-unknown}    latest: $latest"
  if [ -n "$cur" ] && [ "$cur" = "$latest" ]; then ok "already up to date ($cur)"; return 0; fi
  tmp="$(mktemp -d)"
  hdr "Downloading + installing $latest…"
  # No --allow-downgrades: self-update only moves forward; a forced downgrade is never a normal update.
  if ! curl $_CURL_HTTPS -fsSL "$url" -o "$tmp/bhserve.deb"; then
    rm -rf "$tmp"; no "self-update failed (download error)"; return 1
  fi
  # Install via `dpkg -i` (upgrades in place) then `apt-get -f install` to pull any new deps — NOT
  # `apt-get install <path.deb>`, which fails on apt 2.9+ (Ubuntu 25.04+) with "Unsupported file".
  # dpkg -i exits non-zero when a dep is missing (leaves the pkg unpacked); apt-get -f then configures
  # it + installs the deps — so run apt-get -f unconditionally, then VERIFY the installed version.
  $SUDO env DEBIAN_FRONTEND=noninteractive dpkg -i "$tmp/bhserve.deb" >/dev/null 2>&1 || true
  $SUDO env DEBIAN_FRONTEND=noninteractive apt-get -f install -y >/dev/null 2>&1 || true
  rm -rf "$tmp"
  local now; now="$(dpkg-query -W -f='${Version}' bhserve 2>/dev/null || true)"
  if [ "$now" = "$latest" ]; then
    ok "updated ${cur:-?} → $latest — restart the BHServe app if it's open"; return 0
  fi
  no "self-update failed (dpkg/apt error) — grab the .deb from the releases page and run: sudo dpkg -i bhserve_${latest}_all.deb && sudo apt-get -f install -y"; return 1
}

# ── ionCube loaders (Linux override) ─────────────────────────────────────────
# The shared engine's php_ioncube is macOS-only: it downloads the Darwin ("dar") loader bundle and
# writes zend_extension=…ioncube_loader_dar_<mm>.so — Mach-O binaries Linux PHP can NEVER load. On
# Linux we need (a) the "lin" bundle, and (b) the ini placed in the DISTRO's own conf.d with a 00-
# prefix: ionCube must be the FIRST zend_extension or it aborts ("must appear as the first entry"),
# and Debian/Ondřej enables opcache via /etc/php/<mm>/*/conf.d/10-opcache.ini in the COMPILED-IN scan
# dir, which PHP reads BEFORE our PHP_INI_SCAN_DIR extra dir — so an entry in BHServe's conf.d would
# always load second and abort. 00-bhserve-ioncube.ini in the same distro dir sorts before 10-opcache.
IONCUBE_URL_ARM="https://downloads.ioncube.com/loader_downloads/ioncube_loaders_lin_aarch64.zip"
IONCUBE_URL_X64="https://downloads.ioncube.com/loader_downloads/ioncube_loaders_lin_x86-64.zip"

# On Debian the php key IS the minor version (php@8.1 → 8.1) — no brew binary probe needed
# (the shared php_mm falls back to $BREW_PREFIX/bin/php = the DEFAULT php, i.e. the wrong mm
# for every non-default version).
php_mm(){ php_label "$1"; }

php_ioncube(){
  local target="${1:-all}"
  local dir="$BH_HOME/ioncube"
  # Purge a stale/wrong-platform cache (e.g. macOS "dar" loaders downloaded by the pre-fix code) —
  # a cache dir that exists but has no Linux loader can never work and must not be trusted.
  if [ -d "$dir/ioncube" ] && ! ls "$dir/ioncube"/ioncube_loader_lin_*.so >/dev/null 2>&1; then
    warn "ionCube cache has no Linux loaders (stale or wrong platform) — re-downloading"
    rm -rf "$dir"
  fi
  if [ ! -d "$dir/ioncube" ]; then
    mkdir -p "$dir"; hdr "Downloading ionCube loaders (Linux)"
    local url
    case "$(uname -m)" in aarch64|arm64) url="$IONCUBE_URL_ARM" ;; *) url="$IONCUBE_URL_X64" ;; esac
    curl -fsSL "$url" -o "$dir/loaders.zip" || die "download failed ($url)"
    ( cd "$dir" && unzip -oq loaders.zip ) || die "unzip failed"
    ok "loaders in $dir/ioncube"
  fi
  local IFS='|' key formula probe role
  while read -r key formula probe role; do
    [ "$role" = php ] || continue
    svc_installed "$key" || continue
    [ "$target" = all ] || [ "$target" = "$key" ] || [ "$target" = "$(php_label "$key")" ] || continue
    local mm so; mm="$(php_mm "$key")"
    # The static-php fallback builds are FULLY static — they cannot dlopen shared zend extensions.
    if _is_static_php "$mm"; then
      warn "$key: this PHP is the static fallback build, which can't load shared extensions like ionCube — use a distro/Ondřej PHP for ionCube sites"
      continue
    fi
    so="$dir/ioncube/ioncube_loader_lin_$mm.so"
    if [ ! -f "$so" ]; then
      warn "$key: ionCube ships no Linux loader for PHP $mm yet (bundle covers 7.4 & 8.1–8.5; no 8.0/8.6). WHMCS/Blesta run fine on 8.1–8.3."
      continue
    fi
    local wrote=0 sapi
    for sapi in fpm cli; do
      [ -d "/etc/php/$mm/$sapi/conf.d" ] || continue
      printf 'zend_extension=%s\n' "$so" | $SUDO tee "/etc/php/$mm/$sapi/conf.d/00-bhserve-ioncube.ini" >/dev/null && wrote=1
    done
    # Retire any broken entry the pre-fix code wrote into BHServe's own conf.d (a dar .so there
    # would print "Failed loading" on every fpm start even after this fix).
    rm -f "$BH_HOME/php/conf.d/$mm/00-ioncube.ini" 2>/dev/null || true
    if [ "$wrote" = 1 ]; then ok "ionCube configured for $key (PHP $mm)"
    else warn "$key: no /etc/php/$mm/{fpm,cli}/conf.d found — not an apt-installed PHP?"; fi
  done < <(services)
  info "restart PHP-FPM to load it: bhserve restart all  (verify: bhserve php status)"
}

php_status(){
  # Same as the shared version, but "configured" is detected at the DISTRO conf.d path this
  # platform writes to (the shared version only looks in BHServe's own conf.d).
  local IFS='|' key formula probe role
  hdr "PHP versions"
  while read -r key formula probe role; do
    [ "$role" = php ] || continue
    svc_installed "$key" || continue
    local b="${BREW_PREFIX}/${probe}" ic="no" mm; mm="$(php_mm "$key")"
    [ -f "/etc/php/$mm/fpm/conf.d/00-bhserve-ioncube.ini" ] && ic="yes"
    if "$b" -v 2>/dev/null | grep -qi ioncube; then ic="loaded"; fi
    ok "$(printf '%-10s' "$key") PHP $mm   ionCube: $ic"
  done < <(services)
}

# ── OpenLiteSpeed backend (Linux-only third site server: server=ols) ──────────────────────────────
# nginx stays the front door (:80/:443, TLS, *.test) and proxies OLS-backed sites to OLS on
# 127.0.0.1:8088 — the exact pattern the apache backend uses (:8080). OLS serves the files with
# native .htaccess support and forwards PHP to BHServe's EXISTING php-fpm pools over their unix
# sockets (external FastCGI) — one PHP stack, so ionCube / CA bundle / per-site version switching
# all apply unchanged. .htaccess freshness is two-layer: per-vhost `autoLoadHtaccess 1` (native,
# OLS 1.7+) + an inotify watcher that graceful-restarts OLS on any .htaccess change (covers server
# -level rewrite edge cases). OLS runs via its packaged systemd unit; workers run as the site user
# so they can reach the fpm sockets (0660, user-owned) and site files.
LSWS_ROOT="/usr/local/lsws"
OLS_PORT=8088

ols_installed(){ [ -x "$LSWS_ROOT/bin/lswsctrl" ]; }
have_ols_sites(){ grep -lq "server=ols" "$BH_HOME"/nginx/sites/*.conf 2>/dev/null; }
_bh_site_user(){ echo "${USER_NAME:-$(id -un)}"; }
_bh_site_group(){ echo "${GROUP_NAME:-$(id -gn)}"; }

# The packaged unit name varies (lshttpd on current debs, lsws/openlitespeed as aliases).
_ols_unit(){
  local u
  for u in lshttpd lsws openlitespeed; do
    systemctl cat "$u" >/dev/null 2>&1 && { echo "$u"; return 0; }
  done
  echo ""
}

# Is OLS actually SERVING? The definitive signal is the listener answering on :8088 — pidfiles
# can be stale and the packaged unit flaps (Type=forking + KillMode=none leave pid/unit state
# unreliable), which once produced a false "failed to start" while the server served fine.
ols_running(){
  (exec 3<>"/dev/tcp/127.0.0.1/$OLS_PORT") 2>/dev/null && { exec 3>&- 3<&-; return 0; }
  local p
  for p in /var/run/openlitespeed.pid /tmp/lshttpd/lshttpd.pid; do
    [ -f "$p" ] && pid_alive "$(cat "$p" 2>/dev/null)" && return 0
  done
  pgrep -x litespeed >/dev/null 2>&1
}

# ⚠️ HARD stop of EVERY OLS instance. Needed because (a) the package postinst starts OLS
# OUTSIDE systemd, so `systemctl stop` alone is a no-op on a fresh install, and (b) the unit
# ships KillMode=none, so even a unit stop can leave workers alive. A surviving stock instance
# is fatal to us: it holds *:7080/*:8088 sockets that make the first BHServe-config (re)start
# die with "address already in use" (seen live in the WSL bring-up).
_ols_kill(){
  local u i
  u="$(_ols_unit)"; [ -n "$u" ] && $SUDO systemctl stop "$u" >/dev/null 2>&1 || true
  $SUDO "$LSWS_ROOT/bin/lswsctrl" stop >/dev/null 2>&1 || true
  for i in 1 2 3 4 5 6; do ols_running || break; sleep 1; done
  if ols_running; then
    $SUDO pkill -x litespeed >/dev/null 2>&1 || true
    sleep 1
    ols_running && $SUDO pkill -9 -x litespeed >/dev/null 2>&1 || true
  fi
  $SUDO rm -f /var/run/openlitespeed.pid /tmp/lshttpd/lshttpd.pid 2>/dev/null || true
}

# Always start THROUGH systemd (so the unit tracks the process and boot autostart works);
# fall back to lswsctrl only when no unit exists.
ols_start(){
  ols_installed || { no "OpenLiteSpeed not installed — bhserve install openlitespeed"; return 1; }
  if ols_running; then ok "OpenLiteSpeed already running (:$OLS_PORT)"; return 0; fi
  local u i; u="$(_ols_unit)"
  if [ -n "$u" ]; then $SUDO systemctl start "$u" >/dev/null 2>&1 || true
  else $SUDO "$LSWS_ROOT/bin/lswsctrl" start >/dev/null 2>&1 || true; fi
  for i in 1 2 3 4 5 6 7 8; do ols_running && break; sleep 1; done
  ols_running && ok "OpenLiteSpeed started (:$OLS_PORT)" || { no "OpenLiteSpeed failed to start"; return 1; }
}

ols_stop(){
  ols_installed || return 0
  _ols_kill
  ok "OpenLiteSpeed stopped"
}

# Graceful restart — zero dropped connections; this is the "soft reload" that picks up
# config/vhost/.htaccess changes. Via systemd's ExecReload (lswsctrl restart) so the unit
# keeps tracking the re-exec'd process; direct lswsctrl only without a unit.
ols_reload(){
  ols_installed || return 0
  ols_running || return 0
  local u; u="$(_ols_unit)"
  if [ -n "$u" ] && systemctl is-active --quiet "$u" 2>/dev/null; then
    $SUDO systemctl reload "$u" >/dev/null 2>&1 || true
  else
    $SUDO "$LSWS_ROOT/bin/lswsctrl" restart >/dev/null 2>&1 || true
  fi
}

# Install from LiteSpeed's official Debian/Ubuntu repo (their documented repo.litespeed.sh
# bootstrapper, downloaded then executed — not piped). Also installs inotify-tools for the
# .htaccess watcher, rewrites the server config as a BHServe-managed file, and installs the
# watcher unit. Idempotent.
ols_install(){
  if ols_installed; then _ols_configure; return 0; fi
  hdr "Installing OpenLiteSpeed  (LiteSpeed apt repo)"
  _apt_update_once
  if [ ! -f /etc/apt/sources.list.d/lst_debian_repo.list ] && ! ols_installed; then
    local _rt; _rt="$(mktemp)"
    if curl $_CURL_HTTPS -fsSL https://repo.litespeed.sh -o "$_rt"; then
      $SUDO bash "$_rt" >/dev/null 2>&1 || $SUDO bash "$_rt" || { rm -f "$_rt"; no "could not add the LiteSpeed repo"; return 1; }
      rm -f "$_rt"
    else rm -f "$_rt"; no "could not download the LiteSpeed repo setup"; return 1; fi
    _APT_UPDATED=false; _apt_update_once
  fi
  # lsphp83 explicitly: OLS's admin console runs on it (admin_php is a symlink into
  # /usr/local/lsws/lsphp83) and the server FATALLY exits at startup when it's missing —
  # don't rely on the repo bootstrapper having pulled it. (Sites still use BHServe's php-fpm.)
  _apt install -y openlitespeed lsphp83 inotify-tools || { no "apt install openlitespeed failed"; return 1; }
  ols_installed || { no "openlitespeed installed but $LSWS_ROOT/bin/lswsctrl is missing"; return 1; }
  _ols_configure
}

# One-time (idempotent) BHServe configuration of a fresh OLS install:
#  • stop the package's auto-started server (it ships an Example vhost on *:8088)
#  • take ownership of httpd_config.conf (original backed up) — loopback listener, site user,
#    BHSERVE-SITES marker block that _ols_sync_config regenerates
#  • bind the admin console to 127.0.0.1:7080 (never LAN-exposed)
#  • install + enable the .htaccess watcher unit
_ols_configure(){
  local conf="$LSWS_ROOT/conf/httpd_config.conf"
  if ! $SUDO grep -q "BHSERVE-SITES-BEGIN" "$conf" 2>/dev/null; then
    # Take over from the package's stock instance: HARD kill (it runs outside systemd — see
    # _ols_kill), then a later COLD start with our config. Never a graceful restart across the
    # config change — the listener-address change (loopback rebind) makes graceful die fatally.
    _ols_kill
    local u; u="$(_ols_unit)"
    [ -n "$u" ] && $SUDO systemctl enable "$u" >/dev/null 2>&1 || true
    $SUDO cp "$conf" "$conf.bhserve-orig" 2>/dev/null || true
    local tmp; tmp="$(mktemp)"
    cat > "$tmp" <<OLSCONF
# BHServe-managed OpenLiteSpeed config (package original: httpd_config.conf.bhserve-orig).
# Site vhosts + the loopback listener live between the BHSERVE-SITES markers below and are
# REGENERATED by BHServe on every site change — edit anything else freely.
serverName                bhserve
user                      $(_bh_site_user)
group                     $(_bh_site_group)
priority                  0
inMemBufSize              60M
swappingDir               /tmp/lshttpd/swap
autoFix503                1
gracefulRestartTimeout    15
mime                      conf/mime.properties
showVersionNumber         0
adminEmails

errorlog logs/error.log {
  logLevel                WARN
  debugLevel              0
  rollingSize             10M
  enableStderrLog         1
}
accesslog logs/access.log {
  rollingSize             10M
  keepDays                7
  compressArchive         0
}
indexFiles                index.php, index.html

expires  {
  enableExpires           1
}
tuning  {
  maxConnections          2000
  maxSSLConnections       200
  connTimeout             300
  maxKeepAliveReq         1000
  keepAliveTimeout        5
  maxReqURLLen            32768
  maxReqHeaderSize        65536
  maxReqBodySize          2047M
  maxDynRespHeaderSize    32768
  maxDynRespSize          2047M
  maxCachedFileSize       4096
  totalInMemCacheSize     20M
  maxMMapFileSize         256K
  totalMMapCacheSize      40M
  useSendfile             1
  fileETag                28
  enableGzipCompress      1
  compressibleTypes       default
  enableDynGzipCompress   1
  gzipCompressLevel       6
}
fileAccessControl  {
  followSymbolLink        1
  checkSymbolLink         0
  requiredPermissionMask  000
  restrictedPermissionMask 000
}
perClientConnLimit  {
  staticReqPerSec         0
  dynReqPerSec            0
  outBandwidth            0
  inBandwidth             0
}
accessDenyDir  {
  dir                     /etc/*
  dir                     /dev/*
  dir                     conf/*
  dir                     admin/conf/*
}
accessControl  {
  allow                   ALL
}
# BHSERVE-SITES-BEGIN
# BHSERVE-SITES-END
OLSCONF
    $SUDO cp "$tmp" "$conf"; rm -f "$tmp"
    ok "OpenLiteSpeed config now BHServe-managed (loopback :$OLS_PORT, workers run as $(_bh_site_user))"
  fi
  # Admin console: loopback only. (Password stays whatever the package generated — see
  # $LSWS_ROOT/adminpasswd if it wrote one; the console is optional for BHServe.)
  local aconf="$LSWS_ROOT/admin/conf/admin_config.conf"
  if $SUDO grep -qE '^\s*address\s+\*:7080' "$aconf" 2>/dev/null; then
    $SUDO sed -i 's/^\(\s*address\s\+\)\*:7080/\1127.0.0.1:7080/' "$aconf" || true
    ok "OLS admin console bound to 127.0.0.1:7080"
  fi
  _ols_watcher_install
}

# The .htaccess watcher: inotify on the sites root; any .htaccess change → graceful OLS restart.
# Belt-and-suspenders on top of per-vhost autoLoadHtaccess. Root unit (lswsctrl needs root);
# debounced; exits cleanly when inotify-tools is missing.
_ols_watcher_install(){
  local script=/usr/local/lib/bhserve/ols-htaccess-watch.sh
  local unit=/etc/systemd/system/bhserve-ols-watch.service
  local sroot; sroot="$(jget sites_root "$HOME/BHServe/www")"
  # Idempotent + self-updating: skip only when the CURRENT script version is installed AND the
  # watcher is alive; otherwise (first install, script upgrade, dead unit) (re)deploy + restart.
  # Called from _ols_apply too, so every site change heals the watcher — bump the V-tag on edits.
  if [ -f "$script" ] && grep -q "BHSERVE-OLSWATCH-V3" "$script" 2>/dev/null \
     && systemctl is-active --quiet bhserve-ols-watch 2>/dev/null; then return 0; fi
  $SUDO mkdir -p /usr/local/lib/bhserve
  $SUDO tee "$script" >/dev/null <<'WATCH'
#!/bin/bash
# BHSERVE-OLSWATCH-V3
# BHServe: graceful-restart OpenLiteSpeed when any site's .htaccess changes.
# ⚠️ MUST be monitor mode (-m): single-shot `inotifywait -r` only watches directories that
# existed when it started — a site created LATER was invisible to it (found the hard way).
# ⚠️ MUST NOT use --include: with it, events in directories created AFTER start never surface
# (verified live) — so we take ALL events and filter for .htaccess in the loop instead.
# If inotifywait ever dies, the pipe ends, we exit, and systemd (Restart=always) relaunches us.
ROOT="${1:?usage: ols-htaccess-watch.sh <sites-root>}"
command -v inotifywait >/dev/null 2>&1 || exit 0
[ -d "$ROOT" ] || exit 0
inotifywait -m -q -r -e close_write -e create -e delete -e moved_to \
  --format '%w%f' "$ROOT" 2>/dev/null | while read -r _f; do
  case "$_f" in */.htaccess) ;; *) continue ;; esac
  sleep 2                                   # debounce editor save bursts...
  while read -r -t 0.3 _f; do :; done       # ...and drain whatever queued meanwhile
  # graceful reload via systemd (keeps the unit tracking the re-exec'd server); direct fallback
  systemctl reload lshttpd >/dev/null 2>&1 || /usr/local/lsws/bin/lswsctrl restart >/dev/null 2>&1 || true
done
exit 0
WATCH
  $SUDO chmod 0755 "$script"
  $SUDO tee "$unit" >/dev/null <<UNIT
[Unit]
Description=BHServe OpenLiteSpeed .htaccess watcher
After=network.target

[Service]
Type=simple
ExecStart=$script $sroot
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
UNIT
  $SUDO systemctl daemon-reload >/dev/null 2>&1 || true
  $SUDO systemctl enable bhserve-ols-watch >/dev/null 2>&1 || true
  # restart (not just enable --now): a running OLD copy of the script must be replaced
  $SUDO systemctl restart bhserve-ols-watch >/dev/null 2>&1 || true
  ok ".htaccess watcher installed (graceful OLS reload on change)"
}

# Per-site OLS vhost config. PHP goes to the site's EXISTING php-fpm pool over its unix
# socket (UDS) — the same pool nginx-backed sites use. autoLoadHtaccess picks up .htaccess
# edits without restarts (the watcher covers the rest).
render_ols_vhost(){
  local name="$1" domain="$2" root="$3" phpkey="$4"
  local label sock d="$LSWS_ROOT/conf/vhosts/bhserve-$1"
  label="$(php_label "$phpkey")"; sock="$(php_sock "$phpkey")"
  $SUDO mkdir -p "$d"
  $SUDO tee "$d/vhconf.conf" >/dev/null <<OLSVH
# BHServe OLS vhost: $name ($domain) php=$phpkey — regenerated on site changes
docRoot                   $root
vhDomain                  $domain
enableGzip                1

errorlog $BH_HOME/logs/$name-ols-error.log {
  useServer               0
  logLevel                WARN
  rollingSize             10M
}
accesslog $BH_HOME/logs/$name-ols-access.log {
  useServer               0
  rollingSize             10M
  keepDays                7
}

index  {
  useServer               0
  indexFiles              index.php, index.html, index.htm
}

scripthandler  {
  add                     fcgi:bhphp$label php
}

extprocessor bhphp$label {
  type                    fcgi
  address                 UDS:/$sock
  maxConns                20
  initTimeout             60
  retryTimeout            0
  persistConn             1
  respBuffer              0
  autoStart               0
}

rewrite  {
  enable                  1
  autoLoadHtaccess        1
  logLevel                0
  rules                   <<<END_rewrite
RewriteCond %{HTTP:X-Forwarded-Proto} https
RewriteRule .* - [E=HTTPS:on]
END_rewrite
}
OLSVH
  ok "site vhost (OpenLiteSpeed): $d/vhconf.conf"
}

# Heal OLS vhosts written before the HTTPS-forwarding rule (parity #10): re-render any bhserve OLS
# vhconf that lacks the X-Forwarded-Proto→HTTPS rewrite. TLS terminates at nginx and is proxied to
# OLS over plain HTTP, so without this an https-enforcing app (WHMCS/Blesta/WordPress) behind the
# OLS backend sees http → 302→https forever (ERR_TOO_MANY_REDIRECTS). Reconstructs args from the
# nginx vhost, like apache_heal_vhosts. Called from _ols_apply so existing sites self-heal.
_ols_heal_vhosts(){
  ols_installed || return 0
  local f name domain root phpkey vh
  for f in "$BH_HOME"/nginx/sites/*.conf; do
    [ -f "$f" ] || continue
    [ "$(vhost_server "$f")" = ols ] || continue
    name="$(basename "$f" .conf)"
    vh="$LSWS_ROOT/conf/vhosts/bhserve-$name/vhconf.conf"
    $SUDO grep -q 'X-Forwarded-Proto' "$vh" 2>/dev/null && continue
    domain="$(awk '/server_name/{print $2; exit}' "$f" | tr -d ';')"
    root="$(awk '/^[[:space:]]*root /{print $2; exit}' "$f" | tr -d ';')"
    phpkey="$(sed -n 's/.*php=\([^[:space:]]*\).*/\1/p' "$f" | head -1)"
    [ -n "$domain" ] && [ -n "$root" ] && [ -n "$phpkey" ] \
      && render_ols_vhost "$name" "$domain" "$root" "$phpkey" >/dev/null 2>&1 || true
  done
}

# nginx front that proxies the whole host to OLS — mirror of the apache front.
render_nginx_ols_proxy_vhost(){
  local name="$1" domain="$2" root="$3" phpkey="$4" conf="$BH_HOME/nginx/sites/$1.conf"
  cat > "$conf" <<NGINX
# BHServe site: $name  ($domain)  php=$phpkey server=ols
server {
$(nginx_listen_block "$domain")
    server_name $domain;
    root $root;   # served by OpenLiteSpeed (:$OLS_PORT); kept for tooling/metadata

    access_log $BH_HOME/logs/$name-access.log;
    error_log  $BH_HOME/logs/$name-error.log;

    location / {
        proxy_pass http://127.0.0.1:$OLS_PORT;
        proxy_set_header Host \$host;
        proxy_set_header X-Real-IP \$remote_addr;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_read_timeout 600;
    }
}
NGINX
  ok "site vhost (nginx→OpenLiteSpeed): $conf"
}

# Regenerate the BHSERVE-SITES block in httpd_config.conf from the site list (every vhost with
# server=ols gets a virtualhost def + a listener map). Deterministic — full block rewrite.
_ols_sync_config(){
  ols_installed || return 0
  local conf="$LSWS_ROOT/conf/httpd_config.conf"
  $SUDO test -f "$conf" || return 0
  local block maps="" f name domain root
  block="$(mktemp)"
  {
    echo "# BHSERVE-SITES-BEGIN"
    for f in "$BH_HOME"/nginx/sites/*.conf; do
      [ -f "$f" ] || continue
      [ "$(vhost_server "$f")" = ols ] || continue
      name="$(basename "$f" .conf)"
      # ALL server_name entries (canonical + subdomain aliases), comma-joined for the OLS
      # listener map — mapping only $2 (the canonical) left aliases unrouted: OLS got the
      # subdomain's Host, matched nothing, and served the wrong site.
      domain="$(awk '/server_name/{for(i=2;i<=NF;i++){gsub(/;/,"",$i); printf "%s%s",(i>2?", ":""),$i}; exit}' "$f")"
      root="$(awk '/^[[:space:]]*root /{print $2; exit}' "$f" | tr -d ';')"
      printf 'virtualhost bhserve-%s {\n  vhRoot                  %s\n  configFile              conf/vhosts/bhserve-%s/vhconf.conf\n  allowSymbolLink         1\n  enableScript            1\n  restrained              0\n}\n' "$name" "$root" "$name"
      maps="$maps  map                     bhserve-$name $domain
"
    done
    if [ -n "$maps" ]; then
      printf 'listener BHServe {\n  address                 127.0.0.1:%s\n  secure                  0\n%s}\n' "$OLS_PORT" "$maps"
    fi
    echo "# BHSERVE-SITES-END"
  } > "$block"
  local cur new; cur="$(mktemp)"; new="$(mktemp)"
  $SUDO cat "$conf" > "$cur"
  awk -v bf="$block" '
    /# BHSERVE-SITES-BEGIN/ { skip=1; while ((getline line < bf) > 0) print line; next }
    /# BHSERVE-SITES-END/   { skip=0; next }
    !skip { print }
  ' "$cur" > "$new"
  grep -q "BHSERVE-SITES-BEGIN" "$new" || cat "$block" >> "$new"
  $SUDO cp "$new" "$conf"
  rm -f "$block" "$cur" "$new"
}

# Sync the OLS server config to the current site list and apply it (graceful, or first start).
_ols_apply(){
  ols_installed || return 0
  _ols_watcher_install   # self-heals/updates the watcher (no-op when current + running)
  _ols_heal_vhosts       # add the X-Forwarded-Proto→HTTPS rule to any pre-#10 vhost (parity #10)
  _ols_sync_config
  if ols_running; then ols_reload; else have_ols_sites && ols_start || true; fi
}

# ── Overrides: teach the shared site verbs the third backend ─────────────────────────────────────
# render_site_vhost: add the ols branch (nginx/apache branches identical to the shared engine).
render_site_vhost() {
  local name="$1" domain="$2" root="$3" phpkey="$4" server="${5:-nginx}"
  case "$server" in
    apache)
      render_apache_vhost "$name" "$domain" "$root" "$phpkey"
      render_nginx_proxy_vhost "$name" "$domain" "$root" "$phpkey" ;;
    ols|openlitespeed)
      render_ols_vhost "$name" "$domain" "$root" "$phpkey"
      render_nginx_ols_proxy_vhost "$name" "$domain" "$root" "$phpkey" ;;
    *)
      render_nginx_php_vhost "$name" "$domain" "$root" "$phpkey" ;;
  esac
}

# site add: accept --server ols by creating via the shared path (as nginx — full reuse of
# provisioning/auto-secure/landing page) then switching the fresh site to OLS.
eval "$(declare -f site_add | sed '1s/^site_add/_bh_shared_site_add/')"
site_add(){
  local want_ols=0 prev="" a; local -a out=()
  for a in "$@"; do
    if [ "$prev" = "--server" ] && { [ "$a" = ols ] || [ "$a" = openlitespeed ]; }; then
      want_ols=1; a=nginx
    fi
    out+=("$a"); prev="$a"
  done
  if [ "$want_ols" = 1 ]; then ols_install || die "OpenLiteSpeed install failed"; fi
  _bh_shared_site_add "${out[@]}"
  if [ "$want_ols" = 1 ]; then site_set_server "$1" ols; fi
}

# site server: nginx|apache|ols (full replacement — the shared one only knows nginx|apache).
site_set_server() {
  [ $# -ge 2 ] || die "usage: bhserve site server <name> <nginx|apache|ols>"
  local name="$1" newsrv="$2"; valid_site_name "$name"
  local conf="$BH_HOME/nginx/sites/$1.conf"
  [ -f "$conf" ] || die "no site '$name'"
  case "$newsrv" in openlitespeed) newsrv=ols ;; esac
  case "$newsrv" in nginx|apache|ols) ;; *) die "server must be nginx, apache or ols" ;; esac
  [ "$newsrv" != apache ] || svc_installed httpd || die "apache needs httpd — bhserve install httpd"
  if [ "$newsrv" = ols ]; then ols_install || die "OpenLiteSpeed install failed"; fi
  local domain root php oldsrv
  oldsrv="$(vhost_server "$conf")"
  domain="$(awk '/server_name/{print $2; exit}' "$conf" | tr -d ';')"
  root="$(awk '/^[[:space:]]*root /{print $2; exit}' "$conf" | tr -d ';')"
  php="$(sed -n 's/.*php=\([^[:space:]]*\).*/\1/p' "$conf" | head -1)"
  [ "$newsrv" = nginx ] && rm -f "$BH_HOME/apache/sites/$name.conf"
  if [ "$oldsrv" = ols ] && [ "$newsrv" != ols ]; then
    $SUDO rm -rf "$LSWS_ROOT/conf/vhosts/bhserve-$name" 2>/dev/null || true
  fi
  render_site_vhost "$name" "$domain" "$root" "$php" "$newsrv"
  [ "$newsrv" = apache ] && apache_start >/dev/null 2>&1 || true
  ok "$name now served by $newsrv"
  apache_reload
  if [ "$newsrv" = ols ] || [ "$oldsrv" = ols ]; then _ols_apply; fi
  maybe_reload_nginx
}

# site php / site root: after the shared logic re-renders, sync + graceful-reload OLS when the
# site is OLS-backed (its extprocessor socket / docRoot changed).
eval "$(declare -f site_set_php | sed '1s/^site_set_php/_bh_shared_site_set_php/')"
site_set_php(){
  _bh_shared_site_set_php "$@"; local rc=$?
  local conf="$BH_HOME/nginx/sites/$1.conf"
  if [ -f "$conf" ] && [ "$(vhost_server "$conf")" = ols ]; then _ols_apply; fi
  return $rc
}
eval "$(declare -f site_set_root | sed '1s/^site_set_root/_bh_shared_site_set_root/')"
site_set_root(){
  _bh_shared_site_set_root "$@"; local rc=$?
  local conf="$BH_HOME/nginx/sites/$1.conf"
  if [ -f "$conf" ] && [ "$(vhost_server "$conf")" = ols ]; then _ols_apply; fi
  return $rc
}

# site subdomain add/rm: OLS's listener/vhost domain map is generated FROM the nginx vhosts
# (_ols_sync_config), so an OLS-backed site's alias change must resync + reload OLS too — else
# the new subdomain reaches nginx but OLS has no mapping for its Host and serves the wrong page.
eval "$(declare -f site_subdomain | sed '1s/^site_subdomain/_bh_shared_site_subdomain/')"
site_subdomain(){
  _bh_shared_site_subdomain "$@"; local rc=$?
  case "${1:-}" in add|rm|remove)
    local conf="$BH_HOME/nginx/sites/${2:-}.conf"
    if [ -f "$conf" ] && [ "$(vhost_server "$conf")" = ols ]; then _ols_apply; fi ;;
  esac
  return $rc
}

# site rm: also drop the OLS vhost + resync when the removed site was OLS-backed.
eval "$(declare -f site_rm | sed '1s/^site_rm/_bh_shared_site_rm/')"
site_rm(){
  local _name="" _a _was_ols=0
  for _a in "$@"; do case "$_a" in --*) ;; *) _name="$_a" ;; esac; done
  if [ -n "$_name" ] && [ -f "$BH_HOME/nginx/sites/$_name.conf" ] \
     && [ "$(vhost_server "$BH_HOME/nginx/sites/$_name.conf")" = ols ]; then _was_ols=1; fi
  _bh_shared_site_rm "$@"; local rc=$?
  if [ "$_was_ols" = 1 ]; then
    $SUDO rm -rf "$LSWS_ROOT/conf/vhosts/bhserve-$_name" 2>/dev/null || true
    _ols_apply
  fi
  return $rc
}

# start/stop: `bhserve start|stop ols`, OLS joins `start all` (when it has sites) + `stop all`.
eval "$(declare -f start_all | sed '1s/^start_all/_bh_shared_start_all/')"
start_all(){
  _bh_shared_start_all "$@"; local rc=$?
  have_ols_sites && ols_start >/dev/null 2>&1 || true
  return $rc
}
eval "$(declare -f cmd_start | sed '1s/^cmd_start/_bh_shared_cmd_start/')"
cmd_start(){
  case "${1:-}" in ols|openlitespeed) ols_start; return $? ;; esac
  _bh_shared_cmd_start "$@"
}
eval "$(declare -f cmd_stop | sed '1s/^cmd_stop/_bh_shared_cmd_stop/')"
cmd_stop(){
  case "${1:-}" in
    ols|openlitespeed) ols_stop; return $? ;;
    all) _bh_shared_cmd_stop "$@"; local rc=$?; ols_running && ols_stop >/dev/null 2>&1 || true; return $rc ;;
  esac
  _bh_shared_cmd_stop "$@"
}

# ── Apache2 main config (Debian) ──────────────────────────────────────────────────────────────────
# The shared render_apache_main writes a Homebrew-shaped config (ServerRoot /opt/httpd, modules under
# /opt/httpd/lib/httpd/…) that Debian's apache2 REJECTS ("ServerRoot must be a valid directory") — so
# Apache never started on Linux (the Services page showed it perpetually "inactive"). Rewrite it for
# Debian: modules live in /usr/lib/apache2/modules, ServerRoot is a real dir, run as the invoking user,
# PidFile where apache_running looks. PHP is proxied to the php-fpm socket (mod_proxy_fcgi), so no CGI
# startup-warning issue. A catch-all default vhost first = no cross-site flash on a server switch.
render_apache_main() {
  local conf="$BH_HOME/apache/httpd.conf" moddir="/usr/lib/apache2/modules"
  mkdir -p "$BH_HOME/apache/sites" "$BH_HOME/run" "$BH_HOME/logs"
  local mime="/etc/mime.types"; [ -f "$mime" ] || mime="/etc/apache2/mime.types"
  cat > "$conf" <<APACHE
# Generated by BHServe (Linux) — apache2 backend behind nginx. Do not edit by hand.
ServerRoot "$BH_HOME/apache"
Listen 127.0.0.1:$APACHE_PORT
LoadModule mpm_event_module $moddir/mod_mpm_event.so
LoadModule authz_core_module $moddir/mod_authz_core.so
LoadModule dir_module $moddir/mod_dir.so
LoadModule mime_module $moddir/mod_mime.so
LoadModule rewrite_module $moddir/mod_rewrite.so
LoadModule headers_module $moddir/mod_headers.so
LoadModule setenvif_module $moddir/mod_setenvif.so
LoadModule env_module $moddir/mod_env.so
LoadModule alias_module $moddir/mod_alias.so
LoadModule proxy_module $moddir/mod_proxy.so
LoadModule proxy_fcgi_module $moddir/mod_proxy_fcgi.so
ServerName localhost
User ${USER_NAME:-$(id -un)}
Group ${GROUP_NAME:-$(id -gn)}
PidFile "$BH_HOME/run/httpd.pid"
DefaultRuntimeDir "$BH_HOME/run"
Mutex file:$BH_HOME/run default
TypesConfig "$mime"
DirectoryIndex index.php index.html index.htm
ErrorLog "$BH_HOME/logs/apache-error.log"
LogLevel warn
LimitRequestBody 0
<Directory />
    AllowOverride none
    Require all denied
</Directory>
# Catch-all default vhost FIRST → an unmatched Host (e.g. a vhost momentarily missing during a
# server switch) gets a clean deny, never another site's page.
<VirtualHost 127.0.0.1:$APACHE_PORT>
    ServerName bhserve-default.invalid
    <Location "/">
        Require all denied
    </Location>
</VirtualHost>
IncludeOptional $BH_HOME/apache/sites/*.conf
APACHE
  ok "apache main config: $conf"
}

# ── Clean version strings for the Linux web servers ─────────────────────────────────────────────────
# The shared probe_version special-cases *httpd* → `-v`, but Debian's binary is /usr/sbin/apache2
# (no match) → it fell to `apache2 --version`, which prints a TIMESTAMPED [core:warn] line. That value
# CHANGES every 4s api refresh → the Services page recomputed its signature and rebuilt the whole list
# → constant flashing, and the rebuild reset the auto-start star mid-click. OpenLiteSpeed's probe
# (lswsctrl) isn't a version command either. Give both a stable, clean version; delegate the rest.
eval "$(declare -f probe_version | sed '1s/^probe_version/_bh_shared_probe_version/')"
probe_version(){
  case "$1" in
    */apache2)
      printf '%s' "$("$1" -v 2>/dev/null | grep -im1 'Apache/')" | tr -d '\n' | cut -c1-46; return ;;
    */lswsctrl|*/lshttpd|*/lsws)
      local v; v="$(head -1 /usr/local/lsws/VERSION 2>/dev/null)"
      [ -n "$v" ] && { printf 'OpenLiteSpeed %s' "$v"; return; }
      printf '%s' "$(/usr/local/lsws/bin/lshttpd -v 2>/dev/null | grep -io 'LiteSpeed/[0-9.]*' | head -1)"; return ;;
  esac
  _bh_shared_probe_version "$1"
}

# Linux-only verb, not in the shared dispatch: intercept it here (platform-linux.sh is sourced just
# before the shared `case`), so the shared engine + macOS build stay untouched.
case "${1:-}" in
  self-update|self_update|selfupdate) cmd_self_update "${@:2}"; exit $? ;;
esac
