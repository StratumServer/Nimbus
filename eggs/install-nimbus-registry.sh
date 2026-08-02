#!/bin/bash
# Standalone Nimbus registry installer. Source of the registry egg's
# scripts.installation.script field. Since v0.2.0 the release zip ships the registry
# (release_full/Nimbus.Registry), so the install downloads it like the proxy egg does
# instead of building from source. Server files land in /mnt/server.
set -euo pipefail

apt-get update -qq && apt-get install -y -qq curl unzip > /dev/null

NIMBUS_DOWNLOAD_URL="${NIMBUS_DOWNLOAD_URL:-https://github.com/StratumServer/Nimbus/releases/download/v0.3.0/Nimbus-v0.3.0.zip}"

# Every download goes through here: https only, redirects included, so a panel
# variable pointing at plain http fails the install rather than fetching over it.
fetch() {
  curl -sSL --proto '=https' --proto-redir '=https' --fail -o "$1" "$2"
}

cd /mnt/server

echo "Downloading the Nimbus release..."
fetch /tmp/nimbus.zip "${NIMBUS_DOWNLOAD_URL}"
rm -rf /tmp/nimbus && mkdir -p /tmp/nimbus
unzip -qo /tmp/nimbus.zip -d /tmp/nimbus
REGISTRY_DIR=$(find /tmp/nimbus -type d -name "Nimbus.Registry" | head -1)
if [ -z "${REGISTRY_DIR}" ]; then
  echo "Nimbus.Registry folder not found in the release zip (needs v0.2.0 or newer)" >&2
  exit 1
fi
cp -r "${REGISTRY_DIR}"/. /mnt/server/
rm -rf /tmp/nimbus /tmp/nimbus.zip

# The registry reads nimbus.registry.toml next to the binary. Written once here from the
# egg variables (Wings has no TOML parser, so panel-variable changes need a reinstall or
# a manual edit of this file).
if [ ! -f nimbus.registry.toml ]; then
  cat > nimbus.registry.toml <<EOF
bind_url = "http://0.0.0.0:${SERVER_PORT:-8765}"
shared_secret = "${NIMBUS_SHARED_SECRET:-change-me-and-keep-secret}"
EOF
fi

echo "Install complete."
