#!/bin/bash
# Standalone Nimbus registry installer. Source of the registry egg's
# scripts.installation.script field. The registry is not shipped in the release zip,
# so it is built from source; the install container is the .NET 10 SDK image.
# Server files land in /mnt/server.
set -euo pipefail

apt-get update -qq && apt-get install -y -qq git > /dev/null

NIMBUS_GIT_REPO="${NIMBUS_GIT_REPO:-https://github.com/StratumServer/Nimbus.git}"
NIMBUS_GIT_REF="${NIMBUS_GIT_REF:-main}"

echo "Building the Nimbus registry from ${NIMBUS_GIT_REPO}@${NIMBUS_GIT_REF}..."
rm -rf /tmp/nimbus-src
git clone --depth 1 --branch "${NIMBUS_GIT_REF}" "${NIMBUS_GIT_REPO}" /tmp/nimbus-src
dotnet publish /tmp/nimbus-src/Nimbus.Registry/Nimbus.Registry.csproj \
  -c Release -o /mnt/server --nologo
rm -rf /tmp/nimbus-src

cd /mnt/server

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
