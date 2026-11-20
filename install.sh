#!/bin/bash
# Installs Nexus on this Mac or Linux computer: the programs it needs, its build, and on Linux a Nexus entry in the app
# menu. Then it starts Nexus. Running it again rebuilds Nexus from the code in this folder, and skips the programs
# already installed. With Scry beside this folder, it can install Scry too.
#
# Run it in Terminal with: bash install.sh
# --alongside is how Scry's installer runs this one: it neither asks about Scry nor starts Nexus.

set -Eeuo pipefail
cd "$(dirname "$0")"
root=$(pwd)
data="$root/data"
app="$data/app"
tools="$data/tools"
repos=$(cd "$root/../.." && pwd)
jwt="$repos/Pluperfect.Jwt"
mail="$repos/Pluperfect.Mail"
scry=$(cd "$root/.." && pwd)/Scry
api_port=6163
mysql_version=8.4.13
# The PATH this started with, which the other product's installer gets, so each finds its own programs.
original_path=$PATH
alongside=false
[ "${1:-}" = --alongside ] && alongside=true

step() { printf '\n\033[32m== %s\033[0m\n' "$1"; }
stopped() {
  printf '\n\033[31m%s\033[0m\n' "$1"
  exit 1
}
# -E runs the trap inside functions and $(...) as well; only the top level reports, so a failure is reported once.
trap 'if [ "$BASH_SUBSHELL" -eq 0 ]; then stopped "The install stopped at line $LINENO. The lines above say why."; else exit 1; fi' ERR

os=$(uname -s)
case "$(uname -m)" in
  x86_64 | amd64) arch=x64 ;;
  arm64 | aarch64) arch=arm64 ;;
  *) stopped "Nexus installs on x86-64 and ARM64 computers, and this one is $(uname -m)." ;;
esac
[ "$os" = Darwin ] || [ "$os" = Linux ] || stopped "This installer is for macOS and Linux. On Windows, run install.ps1."

listening() { (exec 3<>"/dev/tcp/127.0.0.1/$1") 2>/dev/null; }

# Installs Linux packages through apt or dnf, which asks for the password: the apt names first, then dnf's.
apt_updated=false
apt_refresh() {
  command -v apt-get >/dev/null 2>&1 || return 0
  if [ "$apt_updated" = false ]; then sudo apt-get update; apt_updated=true; fi
}
packages() {
  if command -v apt-get >/dev/null 2>&1; then
    apt_refresh
    sudo apt-get install -y $1
  elif command -v dnf >/dev/null 2>&1; then
    sudo dnf install -y $2
  else
    stopped "This computer has neither apt nor dnf. Install $1 with its package manager, then run this again."
  fi
}

# The newest apt package whose name matches, since Debian and Ubuntu number some libraries by release. It reads the
# package lists apt_refresh fetched, which runs outside $(...) so its output is not taken for the answer.
apt_newest() {
  command -v apt-cache >/dev/null 2>&1 || return 0
  apt-cache pkgnames | grep -E "$1" | sort -V | tail -n 1
}

has_library() { PATH="$PATH:/usr/sbin:/sbin" ldconfig -p | grep "$1" >/dev/null; }

download() {
  curl -fL --retry 3 --progress-bar -o "$2" "$1"
}

brew_shellenv() {
  for brew in /opt/homebrew/bin/brew /usr/local/bin/brew; do
    if [ -x "$brew" ]; then eval "$("$brew" shellenv)"; return; fi
  done
}

# The SDK that Server/global.json asks for: .NET 10, at least the feature band it names.
has_dotnet() { [ -n "$1" ] && (cd "$root/Server" && "$1" --version) >/dev/null 2>&1; }

has_node() {
  [ -n "$1" ] && [ "$("$1" --version 2>/dev/null | sed 's/^v//; s/\..*//')" -ge 24 ] 2>/dev/null
}

# A MySQL 8 or later server, not MariaDB, as its bin folder.
has_mysql() {
  [ -x "$1/mysqld" ] || return 1
  local version
  version=$("$1/mysqld" --version 2>/dev/null) || return 1
  case "$version" in *MariaDB*) return 1 ;; esac
  [ "$(printf '%s' "$version" | sed -n 's/.*Ver \([0-9]*\)\..*/\1/p')" -ge 8 ] 2>/dev/null
}

# Clones a checkout the build needs when it is missing, installing Git first if it is missing too.
checkout() {
  [ -d "$2" ] && return 0
  if ! command -v git >/dev/null 2>&1; then
    step 'Installing Git'
    if [ "$os" = Darwin ]; then brew install git; else packages git git; fi
  fi
  step "Fetching $1 into $2"
  git clone "$3" "$2"
}

step 'Checking the programs Nexus needs'
if listening "$api_port"; then
  stopped "Something is using port $api_port: Nexus's window, its development copy, or Nexus in Docker. Stop it, then run this again."
fi
command -v curl >/dev/null 2>&1 || packages curl curl

with_scry=false
if [ "$alongside" = false ] && [ -f "$scry/install.sh" ]; then
  read -r -p 'Scry is beside Nexus. Install Scry too? (y/N) ' answer || answer=
  case "$answer" in [Yy]*) with_scry=true ;; esac
fi

mkdir -p "$tools"
if [ "$os" = Darwin ]; then
  command -v brew >/dev/null 2>&1 || brew_shellenv
  if ! command -v brew >/dev/null 2>&1; then
    step 'Installing Homebrew, which installs the rest. It asks for your Mac password.'
    /bin/bash -c "$(curl -fsSL https://raw.githubusercontent.com/Homebrew/install/HEAD/install.sh)"
    brew_shellenv
  fi

  dotnet=$(command -v dotnet || true)
  if has_dotnet "$dotnet"; then echo '.NET 10 is installed.'
  else
    step 'Installing .NET 10. It asks for your Mac password.'
    brew install --cask dotnet-sdk@10
    dotnet=/usr/local/share/dotnet/dotnet
    has_dotnet "$dotnet" || stopped ".NET 10 was installed but $dotnet cannot build Nexus."
  fi

  node=$(command -v node || true)
  if has_node "$node"; then echo 'Node 24 or newer is installed.'
  else
    step 'Installing Node.js'
    brew install node
    node=$(brew --prefix)/bin/node
  fi

  mysql_bin=$(dirname "$(command -v mysqld || echo /none/mysqld)")
  if has_mysql "$mysql_bin"; then echo "MySQL is installed, in $mysql_bin."
  else
    # Nexus runs a MySQL server of its own in the data folder, so Homebrew's MySQL service is never started.
    step 'Installing MySQL'
    brew list --formula mysql@8.4 >/dev/null 2>&1 || brew install mysql@8.4
    mysql_bin="$(brew --prefix mysql@8.4)/bin"
  fi

  mailpit=$(command -v mailpit || true)
  if [ -n "$mailpit" ]; then echo 'Mailpit is installed.'
  else
    step 'Installing Mailpit'
    brew install mailpit
    mailpit=$(brew --prefix)/bin/mailpit
  fi
else
  if ! command -v tar >/dev/null 2>&1 || ! command -v xz >/dev/null 2>&1; then packages 'tar xz-utils' 'tar xz'; fi

  # .NET runs on the system's ICU and OpenSSL.
  if ! has_library libicuuc.so; then apt_refresh; packages "$(apt_newest '^libicu[0-9]+$')" libicu; fi
  if ! has_library libssl.so.3; then apt_refresh; packages "$(apt_newest '^libssl3(t64)?$')" openssl-libs; fi

  dotnet=$(command -v dotnet || true)
  has_dotnet "$dotnet" || dotnet="$tools/dotnet/dotnet"
  if has_dotnet "$dotnet"; then echo ".NET 10 is installed, at $dotnet."
  else
    step 'Downloading .NET 10'
    download https://dot.net/v1/dotnet-install.sh "$tools/dotnet-install.sh"
    bash "$tools/dotnet-install.sh" --channel 10.0 --install-dir "$tools/dotnet" --no-path
    has_dotnet "$dotnet" || stopped ".NET 10 was downloaded but $dotnet cannot build Nexus."
  fi

  node=$(command -v node || true)
  has_node "$node" || node="$tools/node/bin/node"
  if has_node "$node"; then echo "Node 24 or newer is installed, at $node."
  else
    step 'Downloading Node.js 24'
    listing=https://nodejs.org/dist/latest-v24.x
    archive=$(curl -fsSL "$listing/SHASUMS256.txt" | grep -o "node-v24[0-9.]*-linux-$arch\.tar\.xz" | head -n 1)
    [ -n "$archive" ] || stopped "nodejs.org lists no Node 24 for linux-$arch."
    download "$listing/$archive" "$tools/$archive"
    rm -rf "$tools/node" && mkdir -p "$tools/node"
    tar -xJf "$tools/$archive" -C "$tools/node" --strip-components=1
    rm -f "$tools/$archive"
  fi

  mysql_bin=$(dirname "$(command -v mysqld || echo /usr/sbin/mysqld)")
  if has_mysql "$mysql_bin"; then echo "MySQL is installed, in $mysql_bin."
  elif [ -x "$tools/mysql/bin/mysqld" ]; then
    mysql_bin="$tools/mysql/bin"
    echo "MySQL $mysql_version is installed, in $mysql_bin."
  else
    mysql_bin="$tools/mysql/bin"
    step "Downloading MySQL $mysql_version"
    # The minimal archive leaves out debugging builds and tests; MySQL publishes it for x86-64 only.
    if [ "$arch" = x64 ]; then build=linux-glibc2.28-x86_64-minimal; else build=linux-glibc2.28-aarch64; fi
    archive="mysql-$mysql_version-$build.tar.xz"
    download "https://dev.mysql.com/get/Downloads/MySQL-8.4/$archive" "$tools/$archive"
    rm -rf "$tools/mysql" && mkdir -p "$tools/mysql"
    tar -xJf "$tools/$archive" -C "$tools/mysql" --strip-components=1
    rm -f "$tools/$archive"
  fi
  # mysqld needs libaio and libnuma. Ubuntu 24.04 and later name libaio libaio.so.1t64, so a link with the old name
  # goes in a folder of Nexus's own, which start.sh puts on the library path.
  compat="$tools/mysql/compat"
  missing() {
    for program in mysqld mysql mysqladmin; do
      LD_LIBRARY_PATH="$compat${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}" ldd "$mysql_bin/$program" | grep 'not found' || true
    done | sort -u
  }
  if missing | grep libaio >/dev/null; then
    aio=$(ls /usr/lib/*/libaio.so.1t64 /usr/lib64/libaio.so.1t64 2>/dev/null | head -n 1 || true)
    if [ -z "$aio" ]; then
      step "Installing the libaio library MySQL needs"
      if apt-cache show libaio1t64 >/dev/null 2>&1; then packages libaio1t64 libaio; else packages libaio1 libaio; fi
      aio=$(ls /usr/lib/*/libaio.so.1t64 /usr/lib64/libaio.so.1t64 2>/dev/null | head -n 1 || true)
    fi
    if [ -n "$aio" ]; then mkdir -p "$compat" && ln -sf "$aio" "$compat/libaio.so.1"; fi
  fi
  if missing | grep libnuma >/dev/null; then
    step "Installing the libnuma library MySQL needs"
    packages libnuma1 numactl-libs
  fi
  if missing | grep -e libncurses -e libtinfo >/dev/null; then
    step "Installing the ncurses library MySQL's client needs"
    packages libncurses6 ncurses-libs
  fi
  if [ -n "$(missing)" ]; then
    stopped "MySQL still needs these libraries, which this installer could not provide: $(missing | awk '{print $1}' | tr '\n' ' ')"
  fi

  mailpit=$(command -v mailpit || true)
  [ -n "$mailpit" ] || mailpit="$tools/mailpit/mailpit"
  if [ -x "$mailpit" ]; then echo "Mailpit is installed, at $mailpit."
  else
    step 'Downloading Mailpit'
    if [ "$arch" = x64 ]; then build=amd64; else build=arm64; fi
    download "https://github.com/axllent/mailpit/releases/latest/download/mailpit-linux-$build.tar.gz" "$tools/mailpit.tar.gz"
    rm -rf "$tools/mailpit" && mkdir -p "$tools/mailpit"
    tar -xzf "$tools/mailpit.tar.gz" -C "$tools/mailpit" mailpit
    rm -f "$tools/mailpit.tar.gz"
  fi

  # .NET trusts its certificate in Chrome, Edge, and Firefox through certutil, and for OpenSSL through openssl.
  command -v certutil >/dev/null 2>&1 || packages libnss3-tools nss-tools
  command -v openssl >/dev/null 2>&1 || packages openssl openssl
fi
PATH="$(dirname "$node"):$PATH"

checkout Pluperfect.Jwt "$jwt" https://github.com/mnwachukwu/Pluperfect.Jwt.git
checkout Pluperfect.Mail "$mail" https://github.com/mnwachukwu/Pluperfect.Mail.git

step 'Building the service'
rm -rf "$app/api.new"
# Without --disable-build-servers, the compiler and MSBuild servers stay running after the install ends.
"$dotnet" publish "$root/Server/src/Nexus.Service/Nexus.Service.csproj" -c Release -o "$app/api.new" --disable-build-servers
rm -rf "$app/api"
mv "$app/api.new" "$app/api"

step 'Building the pages'
npm ci --prefix "$jwt/client"
npm run build --prefix "$jwt/client"
npm ci --prefix "$root/Client"
VITE_API_URL="https://localhost:$api_port" npm run build --prefix "$root/Client" -- --outDir "$app/site" --emptyOutDir

step "Trusting the certificate for Nexus's addresses, and for https://127.0.0.1:6164, where providers such as Schwab send people back"
# Chrome and Edge on Linux keep certificates in ~/.pki/nssdb, which they make on their first run. Made here when it is
# missing, so the certificate is trusted even before the browser has been opened.
if [ "$os" = Linux ] && [ ! -d "$HOME/.pki/nssdb" ]; then
  mkdir -p "$HOME/.pki/nssdb" && certutil -d "sql:$HOME/.pki/nssdb" -N --empty-password
fi
if "$dotnet" dev-certs https --check --trust >/dev/null 2>&1; then echo 'The certificate is trusted.'
else
  [ "$os" = Darwin ] && echo 'macOS asks for your password to trust it.'
  "$dotnet" dev-certs https --trust || echo 'The certificate is not trusted everywhere, so a browser may warn about Nexus'"'"'s addresses.'
fi
# PEM files in data, which start.ts serves the pages and the service with.
rm -f "$data/certificate.new.pem" "$data/certificate.new.key"
"$dotnet" dev-certs https --export-path "$data/certificate.new.pem" --format Pem --no-password
mv -f "$data/certificate.new.pem" "$data/certificate.pem"
mv -f "$data/certificate.new.key" "$data/certificate.key"

step 'Recording where the programs are'
if [ -n "$mailpit" ] && [ -x "$mailpit" ]; then mailpit_json="\"$mailpit\""; else mailpit_json=null; fi
cat > "$data/install.json.tmp" <<EOF
{
  "dotnet": "$dotnet",
  "node": "$node",
  "mysqlBin": "$mysql_bin",
  "mailpit": $mailpit_json
}
EOF
mv -f "$data/install.json.tmp" "$data/install.json"

if [ "$os" = Linux ]; then
  step 'Putting Nexus in the app menu'
  menu="$HOME/.local/share/applications"
  mkdir -p "$menu"
  cat > "$menu/nexus.desktop.tmp" <<EOF
[Desktop Entry]
Type=Application
Name=Nexus
Comment=Starts Nexus
Exec=sh "$root/start.sh"
Path=$root
Terminal=true
Categories=Development;
EOF
  mv -f "$menu/nexus.desktop.tmp" "$menu/nexus.desktop"
fi

[ "$alongside" = true ] && exit 0

if [ "$with_scry" = true ]; then
  step 'Installing Scry'
  PATH="$original_path" bash "$scry/install.sh" --alongside
  if [ "$os" = Darwin ]; then
    osascript -e "tell application \"Terminal\" to do script \"sh '$scry/start.sh'\"" >/dev/null
  elif command -v gtk-launch >/dev/null 2>&1 && [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then
    gtk-launch scry >/dev/null 2>&1 || echo "Start Scry from the app menu, or in another Terminal with: sh \"$scry/start.sh\""
  else
    echo "Start Scry from the app menu, or in another Terminal with: sh \"$scry/start.sh\""
  fi
fi

step 'Starting Nexus'
echo "To start it again later: sh \"$root/start.sh\"$([ "$os" = Linux ] && echo ', or Nexus in the app menu')."
trap - ERR
exec sh "$root/start.sh"
