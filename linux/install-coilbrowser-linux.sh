#!/usr/bin/env bash
set -euo pipefail

# Installs the Linux build of CoilViewer (command name: coilbrowser).
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
Name=CoilViewer
Comment=Fast keyboard-first image viewer
Exec=$bin_dir/coilbrowser %U
StartupWMClass=coilbrowser
Terminal=false
Categories=Graphics;Viewer;
MimeType=image/png;image/jpeg;image/webp;image/gif;image/bmp;image/tiff;image/svg+xml;
EOF

update-desktop-database "$apps_dir" >/dev/null 2>&1 || true

for mime in image/png image/jpeg image/webp image/gif image/bmp image/tiff image/svg+xml; do
  xdg-mime default coilbrowser.desktop "$mime"
done

echo "Installed CoilViewer (Linux):"
echo "  command: $bin_dir/coilbrowser"
echo "  source:  $script_path"
echo "  desktop: $desktop_path"
echo "  default MIME types: png, jpeg, webp, gif, bmp, tiff, svg"
