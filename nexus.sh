#!/bin/sh
# Builds Nexus's images and starts Nexus in Docker, then opens it in the browser, installing Docker Engine first on
# Linux if it is missing. With Scry beside this folder, it can start Scry too, linked to Nexus.
# From a release's zip, which has no Dockerfile, it loads the images the zip carries, or downloads them.
# Run it with: sh nexus.sh
set -eu
cd "$(dirname "$0")"
export COMPOSE_IGNORE_ORPHANS=1

# Root runs these commands itself, and anyone else through sudo.
no_sudo() {
  echo "This step needs root, and this computer has no sudo. Run this again as root."
  return 1
}
sudo=
if [ "$(id -u)" -ne 0 ]; then
  if command -v sudo >/dev/null 2>&1; then sudo=sudo; else sudo=no_sudo; fi
fi

if ! command -v docker >/dev/null 2>&1; then
  if [ "$(uname -s)" = Darwin ]; then
    echo "Docker is not installed. Install Docker Desktop, start it, then run this again."
    exit 1
  fi
  if [ "$sudo" = no_sudo ]; then no_sudo; fi
  echo "Docker is not installed. Installing Docker Engine with Docker's own script${sudo:+; sudo asks for your password}."
  script=$(mktemp)
  if command -v curl >/dev/null 2>&1; then
    curl -fsSL https://get.docker.com -o "$script"
  elif command -v wget >/dev/null 2>&1; then
    wget -qO "$script" https://get.docker.com
  else
    echo "Installing Docker needs curl or wget, and this computer has neither. Install curl, then run this again."
    exit 1
  fi
  $sudo sh "$script"
  rm -f "$script"
fi

docker=docker
if ! docker info >/dev/null 2>&1; then
  if [ "$(uname -s)" = Darwin ]; then
    echo "Docker is not running. Start Docker Desktop, then run this again."
    exit 1
  fi
  docker="${sudo:+$sudo }docker"
  # Enabled as well as started, so Docker starts it again after the computer restarts.
  $docker info >/dev/null 2>&1 || $sudo systemctl enable --now docker
fi
if ! $docker compose version >/dev/null 2>&1; then
  echo "Docker Compose is missing. Install the docker-compose-plugin package, then run this again."
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

# Whether an image this stack runs from the registry is not loaded.
missing() {
  for image in $($docker compose -f "$file" config --images | grep '^ghcr\.io/'); do
    $docker image inspect "$image" >/dev/null 2>&1 || return 0
  done
  return 1
}

# A release's zip carries each product's x86-64 images; anywhere else, or without them, they are downloaded.
images() {
  if ! missing; then
    echo "Starting."
    return 0
  fi
  if [ "$($docker info --format '{{.Architecture}}')" = x86_64 ]; then
    if [ -f nexus-images.tar ]; then
      echo "Loading Nexus from nexus-images.tar, then starting. This takes a few minutes the first time."
      $docker load -i nexus-images.tar
    fi
    if [ "$file" = compose.both.yaml ] && [ -f ../Scry/scry-images.tar ]; then
      echo "Loading Scry from ../Scry/scry-images.tar."
      $docker load -i ../Scry/scry-images.tar
    fi
    missing || return 0
  fi
  echo "Downloading and starting. The first download takes several minutes."
  if ! $docker compose -f "$file" pull; then
    echo "It did not download. If the lines above say denied or unauthorized, sign in once with"
    echo "$docker login ghcr.io, as docs/docker.md shows, then run this again."
    exit 1
  fi
}

# Shows the installer's address, with its install code, while the product is not set up yet.
install_code() {
  if $docker compose -f "$file" logs --no-log-prefix "$1" | grep "is not set up yet"; then
    echo "Open the address above to set $2 up, and enter that install code."
  fi
}

# Asks this computer to trust the authority Caddy made for this product's addresses, the first time it meets it.
trust() {
  cert="$(mktemp)"
  if ! $docker compose -f "$file" cp "$1:/data/caddy/pki/authorities/local/root.crt" "$cert" >/dev/null 2>&1; then
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
        if [ "$sudo" = sudo ]; then
          echo "sudo asks for your password to trust $2 in Docker, so this computer trusts its addresses."
        fi
        { $sudo install -m 644 "$cert" "$target" && $sudo "$update"; } ||
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

open_page() {
  if command -v open >/dev/null 2>&1; then open "$1"; elif command -v xdg-open >/dev/null 2>&1; then xdg-open "$1"; fi
}

if [ -f Dockerfile ]; then
  fetch Pluperfect.Jwt ../../Pluperfect.Jwt https://github.com/mnwachukwu/Pluperfect.Jwt.git
  fetch Pluperfect.Mail ../../Pluperfect.Mail https://github.com/mnwachukwu/Pluperfect.Mail.git
fi

file=compose.yaml
if [ -f ../Scry/compose.yaml ]; then
  printf 'Scry is beside Nexus. Start Scry too, linked to Nexus? [y/N] '
  read -r answer
  case "$answer" in [Yy]*) file=compose.both.yaml ;; esac
fi

password . NEXUS_DB_PASSWORD
[ "$file" = compose.both.yaml ] && password ../Scry SCRY_DB_PASSWORD

build=
if [ -f Dockerfile ]; then
  echo "Building and starting. The first build takes several minutes."
  build=--build
else
  images
fi
if ! $docker compose -f "$file" up $build --detach --wait --wait-timeout 600; then
  echo
  echo "It did not start. The lines above say why. If a port is taken, stop the development copy first."
  exit 1
fi

trust nexus-web Nexus
[ "$file" = compose.both.yaml ] && trust scry-web Scry

install_code nexus-api Nexus
[ "$file" = compose.both.yaml ] && install_code scry-api Scry

echo
echo "Nexus is running at https://localhost:6165. Docker keeps it running; docker compose stop stops it."
echo "Nexus's Mailpit, which keeps the mail Nexus sends until Postmark is set up, is at http://localhost:6168."
open_page https://localhost:6165
if [ "$file" = compose.both.yaml ]; then
  echo "Scry is running at https://localhost:6160."
  echo "Scry's Mailpit, which keeps the mail Scry sends until Postmark is set up, is at http://localhost:6162."
  open_page https://localhost:6160
fi
