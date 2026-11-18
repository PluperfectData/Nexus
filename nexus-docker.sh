#!/bin/sh
# Builds Nexus's images and starts Nexus in Docker, then opens it in the browser. With Scry beside this folder, it can
# start Scry too, linked to Nexus. Run it with: sh nexus-docker.sh
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

# A database password, made the first time and kept in .env beside that product's compose.yaml.
password() {
  [ -f "$1/.env" ] && return 0
  printf '%s=%s\n' "$2" "$(head -c 24 /dev/urandom | base64 | tr -d '+/=\n')" > "$1/.env"
}

# Shows the installer's address, with its install code, while the product is not set up yet.
install_code() {
  if docker compose -f "$file" logs --no-log-prefix "$1" | grep "is not set up yet"; then
    echo "Open the address above to set $2 up, and enter that install code."
  fi
}

open_page() {
  if command -v open >/dev/null 2>&1; then open "$1"; elif command -v xdg-open >/dev/null 2>&1; then xdg-open "$1"; fi
}

fetch Pluperfect.Jwt ../../Pluperfect.Jwt https://github.com/mnwachukwu/Pluperfect.Jwt.git
fetch Pluperfect.Mail ../../Pluperfect.Mail https://github.com/mnwachukwu/Pluperfect.Mail.git

file=compose.yaml
if [ -f ../Scry/compose.yaml ]; then
  printf 'Scry is beside Nexus. Start Scry too, linked to Nexus? [y/N] '
  read -r answer
  case "$answer" in [Yy]*) file=compose.both.yaml ;; esac
fi

password . NEXUS_DB_PASSWORD
[ "$file" = compose.both.yaml ] && password ../Scry SCRY_DB_PASSWORD

echo "Building and starting. The first build takes several minutes."
if ! docker compose -f "$file" up --build --detach --wait --wait-timeout 600; then
  echo
  echo "It did not start. The lines above say why. If a port is taken, stop the development copy first."
  exit 1
fi

install_code nexus-api Nexus
[ "$file" = compose.both.yaml ] && install_code scry-api Scry

echo
echo "Nexus is running at http://localhost:6165. Docker keeps it running; docker compose stop stops it."
open_page http://localhost:6165
if [ "$file" = compose.both.yaml ]; then
  echo "Scry is running at http://localhost:6160."
  open_page http://localhost:6160
fi
