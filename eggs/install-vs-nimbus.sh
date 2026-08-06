#!/bin/bash
# Vintage Story dedicated server + Nimbus backend mod installer.
# Source of the egg's scripts.installation.script field (see eggs/README.md).
# Runs in the panel's install container; server files land in /mnt/server.
set -euo pipefail

apt-get update -qq && apt-get install -y -qq curl unzip > /dev/null

VS_VERSION="${VS_VERSION:-1.22.6}"
NIMBUS_DOWNLOAD_URL="${NIMBUS_DOWNLOAD_URL:-https://github.com/StratumServer/Nimbus/releases/download/v0.4.0/Nimbus-v0.4.0.zip}"

# The panel hands this install the variable the admin was shown at creation time, which makes
# this the last moment where a network authenticating with a value published in the Nimbus
# repository costs one form field instead of a network-wide rotation (#40). Checked before the
# game server download so a refusal costs nothing. Nothing here can generate the value: this
# backend has to match the secret its registry already runs on, and one minted per container
# fails every heartbeat it signs.
case "${NIMBUS_SHARED_SECRET:-}" in
  ""|"REPLACE-ME-THE-INSTALL-REFUSES-THIS-VALUE"|"change-me-and-keep-secret"|"REPLACE_ME_WITH_A_LONG_RANDOM_STRING")
    echo "Nimbus: the shared secret variable is still a placeholder, and the backend mod treats those as unset, so this server would boot misconfigured and never heartbeat." >&2
    echo "Set NIMBUS_SHARED_SECRET in the panel to the registry.embedded_shared_secret your proxy generated on its first run (or the shared_secret of your standalone registry), then reinstall." >&2
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

echo "Downloading Vintage Story dedicated server ${VS_VERSION}..."
fetch vs_server.tar.gz "https://cdn.vintagestory.at/gamefiles/stable/vs_server_linux-x64_${VS_VERSION}.tar.gz"
tar -xzf vs_server.tar.gz
rm vs_server.tar.gz

echo "Installing the Nimbus backend mod..."
mkdir -p data/Mods data/ModConfig
fetch /tmp/nimbus.zip "${NIMBUS_DOWNLOAD_URL}"
rm -rf /tmp/nimbus && mkdir -p /tmp/nimbus
unzip -qo /tmp/nimbus.zip -d /tmp/nimbus
MOD_DIR=$(find /tmp/nimbus -type d -name "Nimbus.ServerMod" | head -1)
if [[ -z "${MOD_DIR}" ]]; then
  echo "Nimbus.ServerMod folder not found in the release zip" >&2
  exit 1
fi
rm -rf data/Mods/Nimbus.ServerMod
cp -r "${MOD_DIR}" data/Mods/
rm -rf /tmp/nimbus /tmp/nimbus.zip

# Initial mod config; the panel's file parser re-stamps these values on every boot,
# so panel variables stay authoritative after the install.
if [[ ! -f data/ModConfig/nimbus-server.json ]]; then
  cat > data/ModConfig/nimbus-server.json <<EOF
{
  "Enabled": true,
  "ServerId": "${NIMBUS_SERVER_ID:-backend-1}",
  "DisplayName": "${NIMBUS_SERVER_ID:-backend-1}",
  "PublicHost": "${NIMBUS_PUBLIC_HOST:-127.0.0.1}",
  "PublicPort": 42421,
  "RegistryUrl": "${NIMBUS_REGISTRY_URL:-http://127.0.0.1:8765}",
  "SharedSecret": "${NIMBUS_SHARED_SECRET}",
  "ReservationRequired": true
}
EOF
fi

echo "Install complete."
