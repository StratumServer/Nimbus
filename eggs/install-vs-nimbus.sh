#!/bin/bash
# Vintage Story dedicated server + Nimbus backend mod installer.
# Source of the egg's scripts.installation.script field (see eggs/README.md).
# Runs in the panel's install container; server files land in /mnt/server.
set -euo pipefail

apt-get update -qq && apt-get install -y -qq curl unzip > /dev/null

VS_VERSION="${VS_VERSION:-1.22.3}"
NIMBUS_DOWNLOAD_URL="${NIMBUS_DOWNLOAD_URL:-https://github.com/StratumServer/Nimbus/releases/download/0.1.0-dev/Nimbus-v0.1.0.zip}"

cd /mnt/server

echo "Downloading Vintage Story dedicated server ${VS_VERSION}..."
curl -sSL --fail -o vs_server.tar.gz \
  "https://cdn.vintagestory.at/gamefiles/stable/vs_server_linux-x64_${VS_VERSION}.tar.gz"
tar -xzf vs_server.tar.gz
rm vs_server.tar.gz

echo "Installing the Nimbus backend mod..."
mkdir -p data/Mods data/ModConfig
curl -sSL --fail -o /tmp/nimbus.zip "${NIMBUS_DOWNLOAD_URL}"
rm -rf /tmp/nimbus && mkdir -p /tmp/nimbus
unzip -qo /tmp/nimbus.zip -d /tmp/nimbus
MOD_DIR=$(find /tmp/nimbus -type d -name "Nimbus.ServerMod" | head -1)
if [ -z "${MOD_DIR}" ]; then
  echo "Nimbus.ServerMod folder not found in the release zip" >&2
  exit 1
fi
rm -rf data/Mods/Nimbus.ServerMod
cp -r "${MOD_DIR}" data/Mods/
rm -rf /tmp/nimbus /tmp/nimbus.zip

# Initial mod config; the panel's file parser re-stamps these values on every boot,
# so panel variables stay authoritative after the install.
if [ ! -f data/ModConfig/nimbus-server.json ]; then
  cat > data/ModConfig/nimbus-server.json <<EOF
{
  "Enabled": true,
  "ServerId": "${NIMBUS_SERVER_ID:-backend-1}",
  "DisplayName": "${NIMBUS_SERVER_ID:-backend-1}",
  "PublicHost": "${NIMBUS_PUBLIC_HOST:-127.0.0.1}",
  "PublicPort": 42421,
  "RegistryUrl": "${NIMBUS_REGISTRY_URL:-http://127.0.0.1:8765}",
  "SharedSecret": "${NIMBUS_SHARED_SECRET:-change-me-and-keep-secret}",
  "ReservationRequired": true
}
EOF
fi

echo "Install complete."
