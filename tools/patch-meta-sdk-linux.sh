#!/usr/bin/env bash
# Linux only. Meta XR Core SDK 85.0.0 doesn't compile in the Linux Editor: Editor/MetaXRSimulator/Installer.cs
# declares `downloadedInstallerPath` only under UNITY_EDITOR_WIN / UNITY_EDITOR_OSX (error CS0103).
# Meta fixed it in 207.0.0 by declaring the variable before the #if. This script embeds 85.0.0 into Packages/
# (git-ignored) and applies the same fix. An embedded package overrides the registry one, so Windows/macOS
# checkouts without this folder keep using the normal package. Close the Unity Editor before running it.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
pkg="$here/../quest-app/Packages/com.meta.xr.sdk.core"
version="85.0.0"
sha1="f8b4cfb2789f06cb55ea30b9a379bf52c98d1af3"
url="https://download.packages.unity.com/com.meta.xr.sdk.core/-/com.meta.xr.sdk.core-$version.tgz"

if [ ! -d "$pkg" ]; then
  tmp="$(mktemp -d)"
  trap 'rm -rf "$tmp"' EXIT
  curl -fL "$url" -o "$tmp/core.tgz"
  echo "$sha1  $tmp/core.tgz" | sha1sum -c -
  tar xzf "$tmp/core.tgz" -C "$tmp"
  mv "$tmp/package" "$pkg"
fi

python3 - "$pkg/Editor/MetaXRSimulator/Installer.cs" <<'EOF'
import sys
path = sys.argv[1]
src = open(path, encoding="utf-8-sig").read()
if 'var downloadedInstallerPath = "";' in src:
    print("Already patched: " + path)
    sys.exit(0)
old = ("#if UNITY_EDITOR_WIN\n            var downloadedInstallerPath =\n")
new = ("            var downloadedInstallerPath = \"\";\n#if UNITY_EDITOR_WIN\n            downloadedInstallerPath =\n")
old_osx = ("#elif UNITY_EDITOR_OSX\n            var downloadedInstallerPath =\n")
new_osx = ("#elif UNITY_EDITOR_OSX\n            downloadedInstallerPath =\n")
src = src.replace("\r\n", "\n")
if src.count(old) != 1 or src.count(old_osx) != 1:
    sys.exit("Installer.cs doesn't look like 85.0.0; not patching " + path)
src = src.replace(old, new).replace(old_osx, new_osx)
open(path, "w", encoding="utf-8").write(src)
print("Patched " + path)
EOF
