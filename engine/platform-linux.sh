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

# ── Service registry ─────────────────────────────────────────────────────────
# key | apt package | version-probe binary (path WITHOUT leading slash, so
#       "$BREW_PREFIX/$probe" == "/usr/…") | role
# Note: no bare "php" row — on Debian every PHP is versioned (php-fpmX.Y); the engine's
# default_php (e.g. 8.4) already resolves to the "php@8.4" key, never bare "php".
services() {
  cat <<'EOF'
php@8.4|php8.4-fpm|usr/sbin/php-fpm8.4|php
php@8.3|php8.3-fpm|usr/sbin/php-fpm8.3|php
php@8.2|php8.2-fpm|usr/sbin/php-fpm8.2|php
php@8.1|php8.1-fpm|usr/sbin/php-fpm8.1|php
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

# ── Binary locators (the few brew-keg paths that BREW_PREFIX="" can't fix) ────
NGINX_BIN(){ echo "/usr/sbin/nginx"; }

# php-fpm is versioned on Debian: php-fpm8.4, php-fpm8.3, …  The bare "php" key maps
# to the configured default version.
php_fpm_bin(){
  local key="$1" v
  if [ "$key" = "php" ]; then v="$(jget default_php 8.4)"; else v="${key#php@}"; fi
  echo "/usr/sbin/php-fpm$v"
}

# ── apt helpers ──────────────────────────────────────────────────────────────
# Privileged runner. In production the GUI elevates the whole verb via pkexec; from a
# terminal this prompts once for sudo. SUDO is overridable for headless/dev use.
SUDO="${BHSERVE_SUDO:-sudo}"
_apt(){ $SUDO DEBIAN_FRONTEND=noninteractive apt-get -o Dpkg::Use-Pty=0 "$@"; }
_APT_UPDATED=false
_apt_update_once(){ $_APT_UPDATED && return 0; _apt update >/dev/null 2>&1 || _apt update || true; _APT_UPDATED=true; }

_is_ubuntu(){ grep -qi ubuntu /etc/os-release 2>/dev/null; }
_codename(){ ( . /etc/os-release 2>/dev/null; echo "${VERSION_CODENAME:-stable}" ); }

# Ondřej Surý's repo provides php7.4–8.4 (+ -fpm) — the PPA on Ubuntu, deb.sury.org on Debian.
_ensure_php_repo(){
  ls /etc/apt/sources.list.d/ 2>/dev/null | grep -qiE 'ondrej|sury' && return 0
  hdr "Adding the Ondřej PHP repository (php7.4–8.4)…"
  if _is_ubuntu; then
    _apt install -y software-properties-common ca-certificates >/dev/null 2>&1 || true
    $SUDO add-apt-repository -y ppa:ondrej/php >/dev/null 2>&1 || $SUDO add-apt-repository -y ppa:ondrej/php
  else
    _apt install -y ca-certificates curl >/dev/null 2>&1 || true
    $SUDO install -d -m 0755 /usr/share/keyrings
    curl -fsSL https://packages.sury.org/php/apt.gpg | $SUDO tee /usr/share/keyrings/sury-php.gpg >/dev/null
    echo "deb [signed-by=/usr/share/keyrings/sury-php.gpg] https://packages.sury.org/php/ $(_codename) main" \
      | $SUDO tee /etc/apt/sources.list.d/sury-php.list >/dev/null
  fi
  _APT_UPDATED=false; _apt_update_once
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
    while IFS='|' read -r key _ _ _; do [ -n "$key" ] && targets+=("$key"); done < <(services)
  else targets=("$@"); fi
  _apt_update_once
  local key pkg v
  for key in "${targets[@]}"; do
    svc_exists "$key" || { warn "unknown service: $key (skipped)"; continue; }
    if svc_installed "$key"; then ok "$key already installed"; continue; fi
    pkg="$(svc_formula "$key")"
    case "$key" in
      php@*)
        v="${key#php@}"; _ensure_php_repo
        hdr "Installing $key  (apt)"
        # shellcheck disable=SC2046
        if _apt install -y $(_php_pkgs "$v"); then _disable_system_unit "php$v-fpm"; ok "$key installed"
        else no "install $key failed"; fi ;;
      nginx)
        hdr "Installing nginx  (apt)"
        if _apt install -y nginx; then _disable_system_unit nginx; ok "nginx installed"; else no "install nginx failed"; fi ;;
      httpd)
        hdr "Installing apache2  (apt)"
        if _apt install -y apache2 libapache2-mod-fcgid; then _disable_system_unit apache2; ok "apache2 installed"; else no "install apache2 failed"; fi ;;
      mariadb|mysql)
        hdr "Installing $pkg  (apt)"
        if _apt install -y "$pkg"; then _disable_system_unit "$key"; ok "$key installed"; else no "install $key failed"; fi ;;
      postgresql@*)
        hdr "Installing $pkg  (apt)"
        if _apt install -y "$pkg"; then _disable_system_unit postgresql; ok "$key installed"; else no "install $key failed"; fi ;;
      mkcert)
        hdr "Installing mkcert + NSS tools  (apt)"
        if _apt install -y mkcert libnss3-tools; then ok "mkcert installed"; else no "install mkcert failed"; fi ;;
      *)
        hdr "Installing $key  ($pkg)"
        if _apt install -y "$pkg"; then ok "$key installed"; else no "install $key failed"; fi ;;
    esac
  done
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
      php@*) _ensure_php_repo; _apt install -y --only-upgrade $(_php_pkgs "${key#php@}") 2>&1 | tail -2 || true ;;
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
