#!/usr/bin/env bash
set -euo pipefail

studio_root="$(cd "$(dirname "$0")/../.." && pwd)"
studio_version="${1:-0.1.0}"
dotnet_bin="${MF_DOTNET:-$studio_root/.tools/dotnet/dotnet}"
app_project="$studio_root/Tools/BuildStudio/src/MyFramework.BuildStudio.App/MyFramework.BuildStudio.App.csproj"
cli_project="$studio_root/Tools/BuildStudio/src/MyFramework.BuildStudio.Cli/MyFramework.BuildStudio.Cli.csproj"
output_root="$studio_root/BuildOutput/BuildStudio/$studio_version"

if [[ ! -x "$dotnet_bin" ]]; then
  echo "dotnet executable not found: $dotnet_bin" >&2
  exit 2
fi
if [[ -e "$output_root" ]]; then
  echo "immutable distribution output already exists: $output_root" >&2
  exit 2
fi
mkdir -p "$output_root"

publish_macos() {
  local rid="$1"
  local architecture="$2"
  local publish_dir="$output_root/.publish-$rid"
  local cli_publish_dir="$output_root/.publish-cli-$rid"
  local app_dir="$output_root/MyFrameworkBuildStudio-$architecture.app"
  mkdir -p "$app_dir/Contents/MacOS" "$app_dir/Contents/Resources"
  "$dotnet_bin" publish "$app_project" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o "$publish_dir"
  "$dotnet_bin" publish "$cli_project" -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false \
    -p:IncludeNativeLibrariesForSelfExtract=true -o "$cli_publish_dir"
  cp -R "$publish_dir/." "$app_dir/Contents/MacOS/"
  cp "$cli_publish_dir/mf-build" "$app_dir/Contents/MacOS/mf-build"
  chmod +x "$app_dir/Contents/MacOS/MyFrameworkBuildStudio" \
    "$app_dir/Contents/MacOS/mf-build"
  sed "s/__VERSION__/$studio_version/g" \
    "$studio_root/Tools/BuildStudio/packaging/Info.plist.template" \
    > "$app_dir/Contents/Info.plist"
  codesign --force --deep --sign "${MF_MAC_SIGN_IDENTITY:--}" "$app_dir"
  ditto -c -k --sequesterRsrc --keepParent "$app_dir" \
    "$output_root/MyFrameworkBuildStudio-$architecture.zip"
  rm -rf "$publish_dir" "$cli_publish_dir"
}

publish_windows() {
  local publish_dir="$output_root/windows-x64"
  mkdir -p "$publish_dir"
  "$dotnet_bin" publish "$app_project" -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None -p:DebugSymbols=false -o "$publish_dir"
  "$dotnet_bin" publish "$cli_project" -c Release -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:DebugType=None -p:DebugSymbols=false -o "$publish_dir/cli"
  cp "$publish_dir/cli/mf-build.exe" "$publish_dir/mf-build.exe"
  rm -rf "$publish_dir/cli"
  (cd "$output_root" && zip -qr "MyFrameworkBuildStudio-windows-x64.zip" "windows-x64")
}

publish_macos osx-arm64 macos-arm64
publish_macos osx-x64 macos-x64
publish_windows

find "$output_root" -type f -maxdepth 3 -print0 | sort -z | xargs -0 shasum -a 256 \
  > "$output_root/SHA256SUMS"
echo "$output_root"
