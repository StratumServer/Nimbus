#!/bin/bash
# Standalone Nimbus registry installer. Source of the registry egg's
# scripts.installation.script field. Since v0.2.0 the release zip ships the registry
# (release_full/Nimbus.Registry), so the install downloads it like the proxy egg does
# instead of building from source. Server files land in /mnt/server.
set -euo pipefail

apt-get update -qq && apt-get install -y -qq curl unzip > /dev/null

NIMBUS_DOWNLOAD_URL="${NIMBUS_DOWNLOAD_URL:-https://github.com/StratumServer/Nimbus/releases/download/v0.5.0/Nimbus-v0.5.0.zip}"

# The panel hands this install the variable the admin was shown at creation time, which makes
# this the last moment where a network authenticating with a value published in the Nimbus
# repository costs one form field instead of a network-wide rotation (#40). Checked before the
# download so a refusal costs nothing. Nothing here can generate the value: the same string has
# to be on every proxy, registry and backend, and one minted per container matches nobody.
case "${NIMBUS_SHARED_SECRET:-}" in
  ""|"REPLACE-ME-THE-INSTALL-REFUSES-THIS-VALUE"|"change-me-and-keep-secret"|"REPLACE_ME_WITH_A_LONG_RANDOM_STRING")
    echo "Nimbus: the shared secret variable is still a placeholder, so this registry would accept heartbeats signed with a publicly known credential." >&2
    echo "Set NIMBUS_SHARED_SECRET in the panel to the value the rest of your network already uses, or to a fresh 'openssl rand -hex 32' if this is its first server, then reinstall." >&2
    exit 1
    ;;
  *)
    # Anything else is the operator's own secret, which this script has no way to check and no
    # business rewriting. Spelled out rather than left to the implicit fall-through so the guard
    # reads as a list of refusals plus an explicit accept.
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
REGISTRY_DIR=$(find /tmp/nimbus -type d -name "Nimbus.Registry" | head -1)
if [[ -z "${REGISTRY_DIR}" ]]; then
  echo "Nimbus.Registry folder not found in the release zip (needs v0.2.0 or newer)" >&2
  exit 1
fi
cp -r "${REGISTRY_DIR}"/. /mnt/server/
rm -rf /tmp/nimbus /tmp/nimbus.zip

# The registry reads nimbus.registry.toml next to the binary. Written once here from the
# egg variables (Wings has no TOML parser, so panel-variable changes need a reinstall or
# a manual edit of this file).
if [[ ! -f nimbus.registry.toml ]]; then
  cat > nimbus.registry.toml <<EOF
bind_url = "http://0.0.0.0:${SERVER_PORT:-8765}"
# The same value every backend puts in "SharedSecret" in its nimbus-server.json, and every
# remote-mode proxy puts in registry.shared_secret. The install above refuses to reach this line
# while it is a placeholder; a registry started on one warns on every boot.
shared_secret = "${NIMBUS_SHARED_SECRET}"
EOF
fi

echo "Install complete."
