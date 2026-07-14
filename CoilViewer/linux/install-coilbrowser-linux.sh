#!/usr/bin/env bash
# Install the Linux CoilBrowser (GTK/Python) as the `coilbrowser` command and
# register it as the default handler for common image types.
#
# The symlink points straight at the script in this repo, so `git pull` / local
# edits take effect with no reinstall. Re-run this script only if you move the
# repo or the desktop/MIME registration gets clobbered by another app.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
script_path="$script_dir/coilbrowser-linux.py"
bin_dir="$HOME/.local/bin"
apps_dir="$HOME/.local/share/applications"
desktop_path="$apps_dir/coilbrowser.desktop"

if [[ ! -f "$script_path" ]]; then
  echo "Expected script not found: $script_path" >&2
  exit 1
fi

mkdir -p "$bin_dir" "$apps_dir"
chmod +x "$script_path"
ln -sf "$script_path" "$bin_dir/coilbrowser"

cat > "$desktop_path" <<EOF
[Desktop Entry]
Type=Application
Name=CoilBrowser
Comment=Fast keyboard-first image browser
Exec=$bin_dir/coilbrowser %U
Terminal=false
Categories=Graphics;Viewer;
MimeType=image/png;image/jpeg;image/webp;image/gif;image/bmp;image/tiff;image/svg+xml;
EOF

update-desktop-database "$apps_dir" >/dev/null 2>&1 || true

for mime in image/png image/jpeg image/webp image/gif image/bmp image/tiff image/svg+xml; do
  xdg-mime default coilbrowser.desktop "$mime"
done

echo "Installed coilbrowser:"
echo "  command: $bin_dir/coilbrowser -> $script_path"
echo "  desktop: $desktop_path"
echo "  default MIME types: png, jpeg, webp, gif, bmp, tiff, svg"
