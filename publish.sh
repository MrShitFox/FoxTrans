#!/usr/bin/env bash
set -euo pipefail

configuration="${1:-Release}"
if [[ "$configuration" != "Release" && "$configuration" != "Debug" ]]; then
  echo "Usage: ./publish.sh [Release|Debug]" >&2
  exit 2
fi

if [[ "$(uname -s)" != "Linux" || "$(uname -m)" != "x86_64" ]]; then
  echo "Linux x64 publishing must run on a Linux x64 host." >&2
  exit 2
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
output_root="$repo_root/artifacts/release"
rid_root="$output_root/linux-x64"
desktop_directory="$rid_root/desktop"
cli_directory="$rid_root/cli"

for directory in "$desktop_directory" "$cli_directory"; do
  parent="$(dirname "$directory")"
  if [[ "$parent" != "$rid_root" ]]; then
    echo "Refusing to clean a staging directory outside '$rid_root'." >&2
    exit 2
  fi
  rm -rf -- "$directory"
  mkdir -p "$directory"
done

dotnet publish "$repo_root/FoxTrans.Desktop/FoxTrans.Desktop.csproj" \
  -c "$configuration" -r linux-x64 --self-contained true -o "$desktop_directory" \
  -p:TrimmerSingleWarn=false
dotnet publish "$repo_root/FoxTrans.Cli/FoxTrans.Cli.csproj" \
  -c "$configuration" -r linux-x64 --self-contained true -o "$cli_directory" \
  -p:TrimmerSingleWarn=false

for expected in "$desktop_directory/FoxTrans" "$cli_directory/FoxTrans.Cli"; do
  mapfile -t files < <(find "$(dirname "$expected")" -maxdepth 1 -type f -printf '%f\n' | sort)
  if [[ "${#files[@]}" -ne 1 || "${files[0]}" != "$(basename "$expected")" ]]; then
    echo "Single-file publish did not contain exactly $(basename "$expected"): ${files[*]}" >&2
    exit 1
  fi
done

desktop_archive="$output_root/FoxTrans-Desktop-linux-x64.tar.gz"
cli_archive="$output_root/FoxTrans-Cli-linux-x64.tar.gz"
rm -f -- "$desktop_archive" "$cli_archive"
tar -C "$desktop_directory" -czf "$desktop_archive" FoxTrans
tar -C "$cli_directory" -czf "$cli_archive" FoxTrans.Cli
ls -lh "$desktop_archive" "$cli_archive"
