#!/bin/sh
# Builds and starts Nexus's -dev containers, opens Nexus in the browser, then watches the folders: each saved file is
# copied into its container, which reloads. Ctrl+C stops them. It can start Scry's beside them, linked.
# Run it with: sh nexus-dev.sh
set -eu
cd "$(dirname "$0")"
export COMPOSE_IGNORE_ORPHANS=1

if ! docker info >/dev/null 2>&1; then
  echo "Docker is not running. Start Docker, then run this again."
  exit 1
fi

# Clones a checkout the build needs, when it is missing.
fetch() {
  [ -d "$2" ] && return 0
  if ! command -v git >/dev/null 2>&1; then
    echo "$1 is missing at $2, and Git is not installed to fetch it. Install Git, or put $1 there, then run this again."
    exit 1
  fi
  echo "Fetching $1 into $2."
  git clone "$3" "$2"
}

# A -dev database's password, made the first time and kept in .env.dev beside that product's compose.dev.yaml,
# apart from the release containers' .env.
password() {
  [ -f "$1/.env.dev" ] && return 0
  printf '%s=%s\n' "$2" "$(head -c 24 /dev/urandom | base64 | tr -d '+/=\n')" > "$1/.env.dev.tmp"
  mv "$1/.env.dev.tmp" "$1/.env.dev"
}

# Asks this computer to trust the authority Caddy made for this product's addresses, the first time it meets it.
trust() {
  cert="$(mktemp)"
  if ! compose cp "$1:/data/caddy/pki/authorities/local/root.crt" "$cert" >/dev/null 2>&1; then
    echo "Could not read $2's certificate authority, so the browser warns about its addresses."
    rm -f "$cert"
    return 0
  fi
  case "$(uname -s)" in
    Darwin)
      sha="$(openssl x509 -noout -fingerprint -sha1 -in "$cert" | cut -d= -f2 | tr -d :)"
      if ! security find-certificate -a -Z | grep -q "$sha"; then
        echo "macOS asks for your password to trust $2 in Docker, so the browser trusts its addresses."
        security add-trusted-cert -r trustRoot -k "$HOME/Library/Keychains/login.keychain-db" "$cert" ||
          echo "$2's certificate authority is not trusted, so the browser warns about its addresses."
      fi
      ;;
    Linux)
      target=
      if command -v update-ca-certificates >/dev/null 2>&1; then
        target="/usr/local/share/ca-certificates/pluperfect-$1.crt"
        update=update-ca-certificates
      elif command -v update-ca-trust >/dev/null 2>&1; then
        target="/etc/pki/ca-trust/source/anchors/pluperfect-$1.crt"
        update=update-ca-trust
      fi
      if [ -n "$target" ] && ! cmp -s "$cert" "$target"; then
        echo "sudo asks for your password to trust $2 in Docker, so this computer trusts its addresses."
        { sudo install -m 644 "$cert" "$target" && sudo "$update"; } ||
          echo "$2's certificate authority is not trusted, so the browser warns about its addresses."
      fi
      # Chrome on Linux reads its own store rather than the system's.
      if command -v certutil >/dev/null 2>&1 && [ -d "$HOME/.pki/nssdb" ]; then
        certutil -d "sql:$HOME/.pki/nssdb" -A -t "C,," -n "Pluperfect $1" -i "$cert" || true
      fi
      ;;
  esac
  rm -f "$cert"
}

# Waits until the address answers, so the browser opens on a page.
wait_for() {
  waited=0
  until curl -fs -o /dev/null "$1" || [ "$waited" -ge 120 ]; do
    sleep 1
    waited=$((waited + 1))
  done
}

open_page() {
  if command -v open >/dev/null 2>&1; then open "$1"; elif command -v xdg-open >/dev/null 2>&1; then xdg-open "$1"; fi
}


fetch Pluperfect.Jwt ../../Pluperfect.Jwt https://github.com/mnwachukwu/Pluperfect.Jwt.git
fetch Pluperfect.Mail ../../Pluperfect.Mail https://github.com/mnwachukwu/Pluperfect.Mail.git

both=
if [ -f ../Scry/compose.dev.yaml ]; then
  printf 'Scry is beside Nexus. Start Scry'"'"'s -dev containers too, linked to Nexus? [y/N] '
  read -r answer
  case "$answer" in [Yy]*) both=1 ;; esac
fi
compose() {
  if [ -n "$both" ]; then docker compose -f compose.dev.both.yaml "$@"; else docker compose -f compose.dev.yaml --env-file .env.dev "$@"; fi
}

password . NEXUS_DEV_DB_PASSWORD
[ -n "$both" ] && password ../Scry SCRY_DEV_DB_PASSWORD

echo "Building and starting the -dev containers. The first build takes several minutes."
if ! compose up --build --detach --wait --wait-timeout 600; then
  echo
  echo "They did not start. The lines above say why. They take the ports development uses, so stop any copy"
  echo "running from the code, or in Docker, first."
  exit 1
fi

trust nexus-callback-dev Nexus
wait_for http://localhost:6165
open_page http://localhost:6165
[ -n "$both" ] && open_page http://localhost:6160
echo
echo "Nexus is at http://localhost:6165, and its service at http://localhost:6163 once it has built. Its Mailpit is"
echo "at http://localhost:6168, and its MySQL answers on 127.0.0.1:6167 for MySQL Workbench, as nexus, with the"
echo "password in Nexus's .env.dev. The install code shows below once the service has started."
if [ -n "$both" ]; then
  echo "Scry is at http://localhost:6160, and its API at http://localhost:6161 once it has built. Its Mailpit is at"
  echo "http://localhost:6162, and its MySQL answers on 127.0.0.1:6166 for MySQL Workbench, as scry, with the password"
  echo "in Scry's .env.dev. The install code shows below once the API has started."
fi
echo "Saved changes reload on their own. Ctrl+C stops the -dev containers."
echo
compose up --watch
