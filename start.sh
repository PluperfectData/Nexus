#!/bin/sh
# Starts the Nexus that install.sh built, on macOS or Linux. Ctrl+C stops it.
# Run it with: sh start.sh
set -eu
cd "$(dirname "$0")"
for brew in /opt/homebrew/bin/brew /usr/local/bin/brew; do
  if [ -x "$brew" ]; then eval "$("$brew" shellenv)"; break; fi
done
if [ -d data/tools/node/bin ]; then PATH="$PWD/data/tools/node/bin:$PATH"; fi
# The link install.sh makes where the system names libaio differently from what MySQL asks for.
if [ -d data/tools/mysql/compat ]; then
  LD_LIBRARY_PATH="$PWD/data/tools/mysql/compat${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"
  export LD_LIBRARY_PATH
fi
if ! command -v node >/dev/null 2>&1; then
  echo "Node is not installed. Run bash install.sh first."
  exit 1
fi
exec node start.ts
