#!/usr/bin/env python3
"""Fast keyboard-first image browser for Linux Mint."""

from __future__ import annotations

import json
import shutil
import sys
from dataclasses import dataclass
from pathlib import Path

import cairo
import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_version("GdkPixbuf", "2.0")
from gi.repository import Gdk, GdkPixbuf, GLib, Gtk  # noqa: E402


IMAGE_EXTS = {
    ".png",
    ".jpg",
    ".jpeg",
    ".jpe",
    ".jfif",
    ".webp",
    ".gif",
    ".bmp",
    ".dib",
    ".tif",
    ".tiff",
    ".svg",
}
CONFIG_PATH = Path.home() / ".config" / "coilbrowser" / "config.json"


@dataclass
class Config:
    background: str = "#000000"
    show_overlay: bool = True
    loop: bool = True
    sort_field: str = "name"
    sort_direction: str = "ascending"


def load_config() -> Config:
    if not CONFIG_PATH.exists():
        CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
        CONFIG_PATH.write_text(json.dumps(Config().__dict__, indent=2), encoding="utf-8")
        return Config()

    data = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
    return Config(**{**Config().__dict__, **data})


def image_files(folder: Path, config: Config) -> list[Path]:
    files = [p for p in folder.iterdir() if p.is_file() and p.suffix.lower() in IMAGE_EXTS]

    def key(path: Path):
        stat = path.stat()
        if config.sort_field == "mtime":
            return stat.st_mtime
        if config.sort_field == "ctime":
            return stat.st_ctime
        if config.sort_field == "size":
            return stat.st_size
        return path.name.lower()

    return sorted(files, key=key, reverse=config.sort_direction == "descending")


def unique_destination(path: Path) -> Path:
    if not path.exists():
        return path

    stem = path.stem
    suffix = path.suffix
    parent = path.parent
    n = 2
    while True:
        candidate = parent / f"{stem}-{n}{suffix}"
        if not candidate.exists():
            return candidate
        n += 1


class CoilBrowser(Gtk.Window):
    def __init__(self, start_path: Path | None):
        super().__init__(title="coilbrowser")
        self.config = load_config()
        self.folder = Path.cwd()
        self.files: list[Path] = []
        self.index = 0
        self.pixbuf: GdkPixbuf.Pixbuf | None = None
        self.zoom = 1.0
        self.pan_x = 0.0
        self.pan_y = 0.0
        self.drag_start: tuple[float, float] | None = None
        self.undo_stack: list[tuple[Path, Path, int]] = []
        self.show_help = False

        self.set_default_size(1200, 800)
        self.fullscreen()

        self.area = Gtk.DrawingArea()
        self.add(self.area)
        self.area.connect("draw", self.on_draw)
        self.area.add_events(
            Gdk.EventMask.SCROLL_MASK
            | Gdk.EventMask.BUTTON_PRESS_MASK
            | Gdk.EventMask.BUTTON_RELEASE_MASK
            | Gdk.EventMask.POINTER_MOTION_MASK
        )
        self.area.connect("scroll-event", self.on_scroll)
        self.area.connect("button-press-event", self.on_button_press)
        self.area.connect("button-release-event", self.on_button_release)
        self.area.connect("motion-notify-event", self.on_motion)

        self.connect("key-press-event", self.on_key)
        self.connect("destroy", Gtk.main_quit)
        self.drag_dest_set(Gtk.DestDefaults.ALL, [], Gdk.DragAction.COPY)
        self.drag_dest_add_uri_targets()
        self.connect("drag-data-received", self.on_drag_data_received)

        self.load_start(start_path)

    def load_start(self, start_path: Path | None) -> None:
        path = start_path.expanduser().resolve() if start_path else Path.cwd()
        if path.is_dir():
            self.folder = path
            self.files = image_files(path, self.config)
            self.index = 0
        else:
            self.folder = path.parent
            self.files = image_files(path.parent, self.config)
            self.index = self.files.index(path) if path in self.files else 0
        self.load_current()

    def load_current(self) -> None:
        if not self.files:
            self.pixbuf = None
            self.queue_draw()
            return
        self.index %= len(self.files)
        self.zoom = 1.0
        self.pan_x = 0.0
        self.pan_y = 0.0
        try:
            self.pixbuf = GdkPixbuf.Pixbuf.new_from_file(str(self.files[self.index]))
        except Exception as exc:
            print(f"failed to load {self.files[self.index]}: {exc}", file=sys.stderr)
            self.pixbuf = None
        self.set_title(f"coilbrowser - {self.files[self.index].name}")
        self.queue_draw()

    def go(self, delta: int) -> None:
        if not self.files:
            return
        new_index = self.index + delta
        if self.config.loop:
            self.index = new_index % len(self.files)
        else:
            self.index = max(0, min(len(self.files) - 1, new_index))
        self.load_current()

    def jump_fraction(self, direction: int) -> None:
        if not self.files:
            return
        target = 0 if direction < 0 else len(self.files) - 1
        self.index = self.index + ((target - self.index) // 2)
        self.load_current()

    def archive_current(self) -> None:
        if not self.files:
            return
        src = self.files[self.index]
        old_dir = src.parent / "old"
        old_dir.mkdir(exist_ok=True)
        dst = unique_destination(old_dir / src.name)
        shutil.move(str(src), str(dst))
        old_index = self.index
        self.undo_stack.append((src, dst, old_index))
        self.files.pop(self.index)
        if self.index >= len(self.files):
            self.index = max(0, len(self.files) - 1)
        self.load_current()

    def undo_archive(self) -> None:
        if not self.undo_stack:
            return
        src, dst, old_index = self.undo_stack.pop()
        if dst.exists():
            shutil.move(str(dst), str(src))
        self.files = image_files(self.folder, self.config)
        self.index = min(old_index, max(0, len(self.files) - 1))
        self.load_current()

    def on_key(self, _widget, event) -> bool:
        key = Gdk.keyval_name(event.keyval) or ""
        ctrl = bool(event.state & Gdk.ModifierType.CONTROL_MASK)
        shift = bool(event.state & Gdk.ModifierType.SHIFT_MASK)

        if key in {"Escape", "q"}:
            Gtk.main_quit()
        elif key in {"Right", "Down", "space"} and ctrl and shift:
            self.jump_fraction(1)
        elif key in {"Left", "Up", "BackSpace"} and ctrl and shift:
            self.jump_fraction(-1)
        elif key in {"Right", "Down", "space"}:
            self.go(1)
        elif key in {"Left", "Up", "BackSpace"}:
            self.go(-1)
        elif key == "Home":
            self.index = 0
            self.load_current()
        elif key == "End":
            self.index = len(self.files) - 1
            self.load_current()
        elif key in {"plus", "equal"}:
            self.zoom_at_center(1.25)
        elif key == "minus":
            self.zoom_at_center(0.8)
        elif key == "backslash":
            self.zoom = 1.0
            self.pan_x = self.pan_y = 0.0
            self.queue_draw()
        elif key in {"i", "I"}:
            self.config.show_overlay = not self.config.show_overlay
            self.queue_draw()
        elif key in {"slash", "question"}:
            self.show_help = not self.show_help
            self.queue_draw()
        elif key in {"a", "A"}:
            self.archive_current()
        elif key == "z" and ctrl:
            self.undo_archive()
        elif key == "F11":
            if self.is_fullscreen():
                self.unfullscreen()
            else:
                self.fullscreen()
        else:
            return False
        return True

    def is_fullscreen(self) -> bool:
        window = self.get_window()
        return bool(window and window.get_state() & Gdk.WindowState.FULLSCREEN)

    def zoom_at_center(self, factor: float) -> None:
        self.zoom = max(0.1, min(20.0, self.zoom * factor))
        self.queue_draw()

    def on_scroll(self, _widget, event) -> bool:
        if self.zoom > 1.01:
            if event.direction == Gdk.ScrollDirection.UP:
                self.pan_y += 80
            elif event.direction == Gdk.ScrollDirection.DOWN:
                self.pan_y -= 80
        elif event.direction == Gdk.ScrollDirection.UP:
            self.go(-1)
        elif event.direction == Gdk.ScrollDirection.DOWN:
            self.go(1)
        self.queue_draw()
        return True

    def on_button_press(self, _widget, event) -> bool:
        if event.type == Gdk.EventType._2BUTTON_PRESS:
            if self.is_fullscreen():
                self.unfullscreen()
            else:
                self.fullscreen()
            return True
        self.drag_start = (event.x - self.pan_x, event.y - self.pan_y)
        return True

    def on_button_release(self, *_args) -> bool:
        self.drag_start = None
        return True

    def on_motion(self, _widget, event) -> bool:
        if self.drag_start and self.zoom > 1.01:
            self.pan_x = event.x - self.drag_start[0]
            self.pan_y = event.y - self.drag_start[1]
            self.queue_draw()
        return True

    def on_drag_data_received(self, _widget, _context, _x, _y, data, _info, _time) -> None:
        uris = data.get_uris()
        if uris:
            path = Path(GLib.filename_from_uri(uris[0])[0])
            self.load_start(path)

    def on_draw(self, widget, cr) -> bool:
        width = widget.get_allocated_width()
        height = widget.get_allocated_height()
        bg = Gdk.RGBA()
        bg.parse(self.config.background)
        cr.set_source_rgba(bg.red, bg.green, bg.blue, bg.alpha)
        cr.rectangle(0, 0, width, height)
        cr.fill()

        if self.pixbuf:
            iw = self.pixbuf.get_width()
            ih = self.pixbuf.get_height()
            base_scale = min(width / iw, height / ih)
            scale = base_scale * self.zoom
            draw_w = iw * scale
            draw_h = ih * scale
            x = (width - draw_w) / 2 + self.pan_x
            y = (height - draw_h) / 2 + self.pan_y
            cr.save()
            cr.translate(x, y)
            cr.scale(scale, scale)
            Gdk.cairo_set_source_pixbuf(cr, self.pixbuf, 0, 0)
            cr.paint()
            cr.restore()
        else:
            self.draw_text(cr, "No images found", 40, 60, 28)

        if self.config.show_overlay:
            self.draw_overlay(cr, width, height)
        if self.show_help:
            self.draw_help(cr, width, height)
        return False

    def draw_overlay(self, cr, width: int, _height: int) -> None:
        if not self.files:
            return
        path = self.files[self.index]
        text = (
            f"{self.index + 1}/{len(self.files)}  {path.name}  "
            f"sort={self.config.sort_field}:{self.config.sort_direction}"
        )
        cr.set_source_rgba(0, 0, 0, 0.65)
        cr.rectangle(0, 0, width, 34)
        cr.fill()
        self.draw_text(cr, text, 12, 23, 16)

    def draw_help(self, cr, width: int, height: int) -> None:
        lines = [
            "Right/Space: next    Left/Backspace: previous",
            "Home/End: first/last    Ctrl+Shift+Arrow: half jump",
            "+/-: zoom    \\: reset zoom    mouse wheel: navigate or pan when zoomed",
            "A: archive to old    Ctrl+Z: undo archive",
            "I: overlay    /: help    F11/double-click: fullscreen    Esc/Q: quit",
        ]
        box_w = min(820, width - 80)
        box_h = 190
        x = (width - box_w) / 2
        y = (height - box_h) / 2
        cr.set_source_rgba(0, 0, 0, 0.8)
        cr.rectangle(x, y, box_w, box_h)
        cr.fill()
        for i, line in enumerate(lines):
            self.draw_text(cr, line, x + 24, y + 38 + i * 28, 18)

    def draw_text(self, cr, text: str, x: float, y: float, size: int) -> None:
        cr.set_source_rgb(1, 1, 1)
        cr.select_font_face("Sans", cairo.FONT_SLANT_NORMAL, cairo.FONT_WEIGHT_NORMAL)
        cr.set_font_size(size)
        cr.move_to(x, y)
        cr.show_text(text)


def main() -> int:
    ok, _ = Gtk.init_check(sys.argv)
    if not ok:
        print("Unable to initialize GTK; is DISPLAY available?", file=sys.stderr)
        return 1
    start = Path(sys.argv[1]) if len(sys.argv) > 1 else None
    window = CoilBrowser(start)
    window.show_all()
    Gtk.main()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
