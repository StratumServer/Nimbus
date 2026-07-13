#!/usr/bin/env python3
"""Regenerates the egg JSON files from the install-*.sh sources.

The .sh files are the readable, reviewable source of each egg's embedded install
script; run `python3 generate.py` from eggs/ after editing one.
"""
import json

AUTHOR = "77785313+Pixnop@users.noreply.github.com"
RUNTIME_IMAGE = {".NET 10": "ghcr.io/parkervcp/yolks:dotnet_10"}
RELEASE_URL = "https://github.com/StratumServer/Nimbus/releases/download/0.1.0-dev/Nimbus-v0.1.0.zip"

def var(name, description, env, default, rules="required|string", field_type="text"):
    return {
        "name": name, "description": description, "env_variable": env,
        "default_value": default, "user_viewable": True, "user_editable": True,
        "rules": rules, "field_type": field_type,
    }

def egg(name, description, startup, done, install_script, install_container, variables,
        config_files=None, stop="^C"):
    return {
        "_comment": "DO NOT EDIT: FILE GENERATED AUTOMATICALLY BY PANEL - PTERODACTYL/PELICAN COMPATIBLE",
        "meta": {"version": "PTDL_v2", "update_url": None},
        "exported_at": "2026-07-13T00:00:00+00:00",
        "name": name,
        "author": AUTHOR,
        "description": description,
        "features": None,
        "docker_images": RUNTIME_IMAGE,
        "file_denylist": [],
        "startup": startup,
        "config": {
            "files": json.dumps(config_files) if config_files else "{}",
            "startup": json.dumps({"done": done}),
            "logs": "{}",
            "stop": stop,
        },
        "scripts": {
            "installation": {
                "script": open(install_script).read(),
                "container": install_container,
                "entrypoint": "bash",
            }
        },
        "variables": variables,
    }

def shared_secret_var(note):
    return var(
        "Nimbus shared secret",
        "HMAC secret of the Nimbus network; every proxy, registry and backend must use the same value. REQUIRED: replace the default with a long random string. " + note,
        "NIMBUS_SHARED_SECRET", "change-me-and-keep-secret", "required|string|max:128")

eggs = {
    "egg-vintage-story-nimbus-backend.json": {
        **egg(
            name="Vintage Story (Nimbus backend)",
            description="Vintage Story dedicated server with the Nimbus backend mod preinstalled, ready to join a Nimbus proxy network. The panel's variables drive nimbus-server.json (server id, registry URL, shared secret) and are re-applied on every boot.",
            startup="dotnet VintagestoryServer.dll --dataPath /home/container/data --port {{SERVER_PORT}}",
            done="Dedicated Server now running",
            install_script="install-vs-nimbus.sh",
            install_container="ghcr.io/parkervcp/installers:debian",
            stop="/stop",
            config_files={
                "data/ModConfig/nimbus-server.json": {
                    "parser": "json",
                    "find": {
                        "ServerId": "{{server.build.env.NIMBUS_SERVER_ID}}",
                        "DisplayName": "{{server.build.env.NIMBUS_SERVER_ID}}",
                        "RegistryUrl": "{{server.build.env.NIMBUS_REGISTRY_URL}}",
                        "SharedSecret": "{{server.build.env.NIMBUS_SHARED_SECRET}}",
                        "PublicHost": "{{server.build.env.NIMBUS_PUBLIC_HOST}}",
                        "PublicPort": "{{server.build.default.port}}",
                        "ReservationRequired": "{{server.build.env.NIMBUS_RESERVATION_REQUIRED}}",
                    },
                }
            },
            variables=[
                var("Vintage Story version",
                    "Game version to install, from the stable CDN (must be 1.19 or newer for Nimbus).",
                    "VS_VERSION", "1.22.3", "required|string|max:20"),
                var("Nimbus release URL",
                    "Download URL of the Nimbus release zip; the Nimbus.ServerMod folder inside it is installed as a mod.",
                    "NIMBUS_DOWNLOAD_URL", RELEASE_URL),
                var("Nimbus server id",
                    "Unique id of this backend in the Nimbus network. Must match the proxy's [servers] entry.",
                    "NIMBUS_SERVER_ID", "backend-1", "required|alpha_dash|max:32"),
                var("Nimbus registry URL",
                    "URL of the Nimbus registry (the proxy's embedded registry bind, or a standalone registry).",
                    "NIMBUS_REGISTRY_URL", "http://127.0.0.1:8765"),
                shared_secret_var("Must match the registry this backend heartbeats to."),
                var("Nimbus public host",
                    "Host address this backend advertises to the registry (how the proxy network reaches it).",
                    "NIMBUS_PUBLIC_HOST", "127.0.0.1", "required|string|max:255"),
                var("Require proxy reservations",
                    "When true, players connecting without a proxy reservation are kicked (recommended: keeps players from bypassing the proxy).",
                    "NIMBUS_RESERVATION_REQUIRED", "true", "required|string|in:true,false"),
            ],
        ),
    },
    "egg-nimbus-proxy.json": egg(
        name="Nimbus proxy",
        description="The Nimbus proxy process: fronts every backend on one address, with the embedded registry serving backend heartbeats over HTTP. The install writes nimbus.proxy.toml from the egg variables; edit that file (or reinstall) for changes, and grow the [servers] pool there for real networks.",
        startup="dotnet Nimbus.Proxy.dll",
        done="listening on",
        install_script="install-nimbus-proxy.sh",
        install_container="ghcr.io/parkervcp/installers:debian",
        variables=[
            var("Nimbus release URL",
                "Download URL of the Nimbus release zip; the proxy bundle inside it is installed.",
                "NIMBUS_DOWNLOAD_URL", RELEASE_URL),
            var("Default backend",
                "host:port of the first backend, written as the starter [servers] entry. Edit nimbus.proxy.toml to grow the pool.",
                "NIMBUS_DEFAULT_BACKEND", "127.0.0.1:42421", "required|string|max:255"),
            var("Embedded registry bind",
                "HTTP URL the embedded registry listens on for backend heartbeats. Keep it reachable from your backend containers.",
                "NIMBUS_EMBEDDED_REGISTRY_BIND", "http://0.0.0.0:8765"),
            shared_secret_var("The proxy refuses to start on a non-loopback registry bind until this is changed."),
        ],
    ),
    "egg-nimbus-registry.json": egg(
        name="Nimbus registry (standalone)",
        description="The standalone Nimbus registry for multi-proxy deployments (single-proxy networks can use the proxy's embedded registry instead). Not shipped in release zips, so the install builds it from source with the .NET 10 SDK. The install writes nimbus.registry.toml from the egg variables; edit that file (or reinstall) for changes.",
        startup="dotnet Nimbus.Registry.dll",
        done="registry listening on",
        install_script="install-nimbus-registry.sh",
        install_container="mcr.microsoft.com/dotnet/sdk:10.0",
        variables=[
            var("Git repository",
                "Repository the registry is built from.",
                "NIMBUS_GIT_REPO", "https://github.com/StratumServer/Nimbus.git"),
            var("Git ref",
                "Branch or tag to build.",
                "NIMBUS_GIT_REF", "main", "required|string|max:100"),
            shared_secret_var("Backends and proxies authenticate against this registry with it."),
        ],
    ),
}

for filename, content in eggs.items():
    with open(filename, "w") as f:
        json.dump(content, f, indent=4)
        f.write("\n")
    print("écrit:", filename)
