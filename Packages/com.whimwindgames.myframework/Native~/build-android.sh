#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: build-android.sh <Android NDK root>" >&2
  exit 2
fi

script_dir="$(cd "$(dirname "$0")" && pwd)"
package_dir="$(cd "$script_dir/.." && pwd)"
toolchain="$1/toolchains/llvm/prebuilt/darwin-x86_64/bin"

for abi in arm64-v8a armeabi-v7a; do
  case "$abi" in
    arm64-v8a) compiler="$toolchain/aarch64-linux-android21-clang" ;;
    armeabi-v7a) compiler="$toolchain/armv7a-linux-androideabi21-clang" ;;
  esac
  output="$package_dir/Runtime/Plugins/Android/libs/$abi/libarcadesha.so"
  "$compiler" -std=c11 -O3 -fPIC -shared \
    -Wl,-z,max-page-size=16384 -Wl,-z,common-page-size=16384 \
    -Wl,-z,relro,-z,now \
    "$script_dir/sha256.c" -o "$output"
done
