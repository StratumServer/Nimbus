#!/bin/bash
# Nimbus proxy installer. Source of the proxy egg's scripts.installation.script field.
# Runs in the panel's install container; server files land in /mnt/server.
set -euo pipefail

apt-get update -qq && apt-get install -y -qq curl unzip > /dev/null

NIMBUS_DOWNLOAD_URL="${NIMBUS_DOWNLOAD_URL:-https://github.com/StratumServer/Nimbus/releases/download/0.1.0-dev/Nimbus-v0.1.0.zip}"

cd /mnt/server

echo "Downloading the Nimbus release..."
curl -sSL --fail -o /tmp/nimbus.zip "${NIMBUS_DOWNLOAD_URL}"
rm -rf /tmp/nimbus && mkdir -p /tmp/nimbus
unzip -qo /tmp/nimbus.zip -d /tmp/nimbus
PROXY_DIR=$(find /tmp/nimbus -type d -name "Nimbus" | head -1)
if [ -z "${PROXY_DIR}" ]; then
  echo "Nimbus proxy folder not found in the release zip" >&2
  exit 1
fi
cp -r "${PROXY_DIR}"/. /mnt/server/
rm -rf /tmp/nimbus /tmp/nimbus.zip

# The proxy reads nimbus.proxy.toml next to the binary. Written once here from the egg
# variables (Wings has no TOML parser, so panel-variable changes need a reinstall or a
# manual edit of this file). The backend pool below is a starter: real networks edit
# [servers] and try directly.
if [ ! -f nimbus.proxy.toml ]; then
  cat > nimbus.proxy.toml <<EOF
bind = "0.0.0.0:${SERVER_PORT:-42420}"
try = [ "default" ]

[servers]
default = "${NIMBUS_DEFAULT_BACKEND:-127.0.0.1:42421}"

[registry]
mode = "embedded"
# Backends heartbeat here; keep it reachable from your backend containers.
embedded_bind = "${NIMBUS_EMBEDDED_REGISTRY_BIND:-http://0.0.0.0:8765}"
# The proxy refuses to start on a non-loopback registry bind until this is changed.
embedded_shared_secret = "${NIMBUS_SHARED_SECRET:-change-me-and-keep-secret}"

[metrics]
enabled = true
bind = "http://127.0.0.1:42500"
EOF
fi

echo "Install complete."
