#!/bin/sh
set -eu

bundle_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
executable="$bundle_dir/FoxTrans"
data_dir="${XDG_DATA_HOME:-$HOME/.local/share}"
applications_dir="$data_dir/applications"
icons_dir="$data_dir/icons/hicolor"
desktop_file="$applications_dir/foxtrans.desktop"

if [ ! -x "$executable" ]; then
  echo "FoxTrans executable was not found or is not executable: $executable" >&2
  exit 1
fi

mkdir -p "$applications_dir" "$icons_dir"
cp -R "$bundle_dir/icons/hicolor/." "$icons_dir/"

desktop_executable=$(printf '%s' "$executable" | sed 's/[\\$`\"]/\\&/g')
while IFS= read -r line; do
  case "$line" in
    'Exec=@FOXTRANS_EXECUTABLE@') printf 'Exec="%s"\n' "$desktop_executable" ;;
    *) printf '%s\n' "$line" ;;
  esac
done < "$bundle_dir/foxtrans.desktop.in" > "$desktop_file"

echo "Installed FoxTrans launcher: $desktop_file"
