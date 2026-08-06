#!/bin/bash
# Nimbus proxy installer. Source of the proxy egg's scripts.installation.script field.
# Runs in the panel's install container; server files land in /mnt/server.
set -euo pipefail

apt-get update -qq && apt-get install -y -qq curl unzip > /dev/null

NIMBUS_DOWNLOAD_URL="${NIMBUS_DOWNLOAD_URL:-https://github.com/StratumServer/Nimbus/releases/download/v0.4.0/Nimbus-v0.4.0.zip}"

# The panel hands this install the variable the admin was shown at creation time, which makes
# this the last moment where a network authenticating with a value published in the Nimbus
# repository costs one form field instead of a network-wide rotation (#40). Checked before the
# download so a refusal costs nothing. Nothing here can generate the value: the same string has
# to be on every proxy, registry and backend, and one minted per container matches nobody.
case "${NIMBUS_SHARED_SECRET:-}" in
  ""|"REPLACE-ME-THE-INSTALL-REFUSES-THIS-VALUE"|"change-me-and-keep-secret"|"REPLACE_ME_WITH_A_LONG_RANDOM_STRING")
    echo "Nimbus: the shared secret variable is still a placeholder, so this install would put a publicly known credential on the network." >&2
    echo "Set NIMBUS_SHARED_SECRET in the panel to the value the rest of your network already uses, or to a fresh 'openssl rand -hex 32' if this is its first server, then reinstall." >&2
    exit 1
    ;;
esac

# Every download goes through here: https only, redirects included, so a panel
# variable pointing at plain http fails the install rather than fetching over it.
fetch() {
  local dest="$1" url="$2"
  curl -sSL --proto '=https' --proto-redir '=https' --fail -o "$dest" "$url"
}

cd /mnt/server

echo "Downloading the Nimbus release..."
fetch /tmp/nimbus.zip "${NIMBUS_DOWNLOAD_URL}"
rm -rf /tmp/nimbus && mkdir -p /tmp/nimbus
unzip -qo /tmp/nimbus.zip -d /tmp/nimbus
PROXY_DIR=$(find /tmp/nimbus -type d -name "Nimbus" | head -1)
if [[ -z "${PROXY_DIR}" ]]; then
  echo "Nimbus proxy folder not found in the release zip" >&2
  exit 1
fi
cp -r "${PROXY_DIR}"/. /mnt/server/
rm -rf /tmp/nimbus /tmp/nimbus.zip

# The proxy reads nimbus.proxy.toml next to the binary. Written once here from the egg
# variables (Wings has no TOML parser, so panel-variable changes need a reinstall or a
# manual edit of this file). The backend pool below is a starter: real networks edit
# [servers] and try directly.
if [[ ! -f nimbus.proxy.toml ]]; then
  cat > nimbus.proxy.toml <<EOF
bind = "0.0.0.0:${SERVER_PORT:-42420}"
try = [ "default" ]

[servers]
default = "${NIMBUS_DEFAULT_BACKEND:-127.0.0.1:42421}"

[registry]
mode = "embedded"
# Backends heartbeat here; keep it reachable from your backend containers.
embedded_bind = "${NIMBUS_EMBEDDED_REGISTRY_BIND:-http://0.0.0.0:8765}"
# The same value every backend puts in "SharedSecret" in its nimbus-server.json. The install
# above refuses to reach this line while it is a placeholder, and the proxy refuses to start on
# a non-loopback registry bind if one gets in here another way.
embedded_shared_secret = "${NIMBUS_SHARED_SECRET}"

[metrics]
enabled = true
bind = "http://127.0.0.1:42500"
EOF
fi

echo "Install complete."
