#!/usr/bin/env python3
"""CoilBrowser - the Linux build of CoilViewer.

A fast, keyboard-first image browser that mirrors the Windows CoilViewer
feature set: instant navigation, sorting, zoom/pan, archive-to-old with undo,
a right-click menu, a settings dialog, neighbor preloading, and an info overlay.
"""

from __future__ import annotations

from concurrent.futures import Future, ThreadPoolExecutor
import json
import os
import shutil
import sys
import time
from dataclasses import asdict, dataclass, fields
from pathlib import Path

BOOT_T0 = time.perf_counter()
_last_startup_mark = BOOT_T0

import cairo
import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
gi.require_version("GdkPixbuf", "2.0")
gi.require_version("Gio", "2.0")
from gi.repository import Gdk, GdkPixbuf, Gio, GLib, Gtk  # noqa: E402

_SCRIPT_DIR = Path(__file__).resolve().parent
if str(_SCRIPT_DIR) not in sys.path:
    sys.path.insert(0, str(_SCRIPT_DIR))
from directory_instance_guard import DirectoryInstanceGuard, resolve_directory  # noqa: E402


APP_NAME = "CoilViewer"
VERSION = "1.0-linux"

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
LOG_DIR = Path.home() / ".local" / "share" / "coilbrowser"

PAN_STEP = 80.0
FOLDER_RESCAN_DEBOUNCE_MS = 400
MAX_ARCHIVE_HISTORY_SIZE = 200

# Mirrors the right-click sort options in the Windows CoilViewer build.
SORT_FIELD_LABELS = {
    "name": "File name",
    "ctime": "Date created",
    "mtime": "Date modified",
    "size": "File size",
}
SORT_MENU_FIELDS = [
    ("File name", "name"),
    ("Date created", "ctime"),
    ("Date modified", "mtime"),
    ("File size", "size"),
]


def _log(filename: str, message: str) -> None:
    try:
        LOG_DIR.mkdir(parents=True, exist_ok=True)
        stamp = time.strftime("%Y-%m-%d %H:%M:%S")
        with (LOG_DIR / filename).open("a", encoding="utf-8") as handle:
            handle.write(f"{stamp} {message}\n")
    except Exception:
        pass


def log(message: str) -> None:
    _log("coilbrowser-launch.log", message)


def log_error(message: str) -> None:
    _log("coilbrowser-errors.log", message)


def startup_mark(label: str) -> None:
    global _last_startup_mark
    now = time.perf_counter()
    log(
        f"[STARTUP] +{(now - BOOT_T0) * 1000:.1f}ms "
        f"(+{(now - _last_startup_mark) * 1000:.1f}ms) {label}"
    )
    _last_startup_mark = now


def format_size(num_bytes: int) -> str:
    size = float(num_bytes)
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if size < 1024 or unit == "TB":
            return f"{int(size)} {unit}" if unit == "B" else f"{size:.1f} {unit}"
        size /= 1024
    return f"{size:.1f} TB"


@dataclass
class Config:
    background: str = "#000000"
    show_overlay: bool = True
    loop: bool = False
    sort_field: str = "name"
    sort_direction: str = "ascending"
    preload_count: int = 20
    fit_mode: str = "Uniform"  # Uniform | UniformToFill
    scaling_mode: str = "HighQuality"  # HighQuality | Fast


def load_config() -> Config:
    start = time.perf_counter()
    defaults = asdict(Config())
    if not CONFIG_PATH.exists():
        CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
        CONFIG_PATH.write_text(json.dumps(defaults, indent=2), encoding="utf-8")
        log(f"[CONFIG] created default config in {(time.perf_counter() - start) * 1000:.1f}ms")
        return Config()

    try:
        data = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
    except Exception as exc:
        log_error(f"failed to read config, using defaults: {exc}")
        log(f"[CONFIG] failed config read in {(time.perf_counter() - start) * 1000:.1f}ms")
        return Config()

    known = {f.name for f in fields(Config)}
    merged = {**defaults, **{k: v for k, v in data.items() if k in known}}
    log(f"[CONFIG] loaded config in {(time.perf_counter() - start) * 1000:.1f}ms")
    return Config(**merged)


def image_files(folder: Path, config: Config) -> list[Path]:
    total_start = time.perf_counter()
    try:
        scan_start = time.perf_counter()
        files = [p for p in folder.iterdir() if p.is_file() and p.suffix.lower() in IMAGE_EXTS]
    except OSError as exc:
        log_error(f"failed to list {folder}: {exc}")
        return []
    scan_ms = (time.perf_counter() - scan_start) * 1000

    def key(path: Path):
        if config.sort_field == "name":
            return path.name.lower()
        stat = path.stat()
        if config.sort_field == "mtime":
            return stat.st_mtime
        if config.sort_field == "ctime":
            return stat.st_ctime
        if config.sort_field == "size":
            return stat.st_size
        return path.name.lower()

    sort_start = time.perf_counter()
    sorted_files = sorted(files, key=key, reverse=config.sort_direction == "descending")
    sort_ms = (time.perf_counter() - sort_start) * 1000
    total_ms = (time.perf_counter() - total_start) * 1000
    log(
        f"[IMAGE_FILES] folder='{folder}' count={len(sorted_files)} "
        f"scan={scan_ms:.1f}ms sort={sort_ms:.1f}ms total={total_ms:.1f}ms "
        f"field={config.sort_field} direction={config.sort_direction}"
    )
    return sorted_files


@dataclass(frozen=True)
class ArchiveStep:
    original_path: Path
    archived_path: Path


def archive_collision_path(target_dir: Path, src: Path) -> Path:
    target = target_dir / src.name
    if not target.exists():
        return target

    stem = src.stem
    suffix = src.suffix
    counter = 1
    while True:
        candidate = target_dir / f"{stem}_{counter}{suffix}"
        if not candidate.exists():
            return candidate
        counter += 1


class CoilBrowser(Gtk.Window):
    def __init__(self, start_path: Path | None):
        startup_mark("CoilBrowser.__init__ entered")
        init_start = time.perf_counter()
        super().__init__(title=APP_NAME)
        self.config = load_config()
        self.folder = Path.cwd()
        self.files: list[Path] = []
        self.index = 0
        self.pixbuf: GdkPixbuf.Pixbuf | None = None
        self.cache: dict[Path, GdkPixbuf.Pixbuf] = {}
        self.cache_is_scaled: dict[Path, bool] = {}
        self.zoom = 1.0
        self.pan_x = 0.0
        self.pan_y = 0.0
        self.drag_start: tuple[float, float] | None = None
        self._archive_history: list[ArchiveStep] = []
        self._status_message: str | None = None
        self._status_seq = 0
        self._directory_guard: DirectoryInstanceGuard | None = None
        self.show_help = False
        self.context_menu: Gtk.Menu | None = None
        self.edge_flash: str | None = None  # "start" or "end" while flashing
        self._flash_seq = 0
        self._first_image_painted = False
        self._startup_load_pending = start_path
        self.loading_message = "Loading..."
        self._preload_queue: list[Path] = []
        self._preload_wanted: set[Path] = set()
        self._preload_source_id: int | None = None
        self._preload_executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="coil-preload")
        self._preload_future: Future[tuple[Path, GdkPixbuf.Pixbuf, bool] | None] | None = None
        self._archive_executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="coil-archive")
        self._folder_monitor: Gio.FileMonitor | None = None
        self._rescan_debounce_id: int | None = None

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
        self.connect("destroy", self.on_destroy)
        self.drag_dest_set(Gtk.DestDefaults.ALL, [], Gdk.DragAction.COPY)
        self.drag_dest_add_uri_targets()
        self.connect("drag-data-received", self.on_drag_data_received)

        log(f"[WINDOW] constructed shell in {(time.perf_counter() - init_start) * 1000:.1f}ms")
        GLib.idle_add(self._load_start_idle)

    def _load_start_idle(self) -> bool:
        path = self._startup_load_pending
        self._startup_load_pending = None
        startup_mark("initial load_start begin after show")
        self.load_start(path)
        return False

    def on_destroy(self, *_args) -> None:
        if self._rescan_debounce_id is not None:
            GLib.source_remove(self._rescan_debounce_id)
            self._rescan_debounce_id = None
        self._stop_folder_monitor()
        self._release_directory_guard()
        self._preload_executor.shutdown(wait=False, cancel_futures=True)
        self._archive_executor.shutdown(wait=False, cancel_futures=True)
        Gtk.main_quit()

    # --- loading -----------------------------------------------------------
    def resolve_path(self, raw: Path) -> Path:
        expanded = os.path.expandvars(str(raw))
        path = Path(expanded).expanduser()
        try:
            return path.resolve()
        except OSError:
            return path

    def load_start(self, start_path: Path | None, preferred_image: Path | None = None) -> None:
        total_start = time.perf_counter()
        path = self.resolve_path(start_path) if start_path else Path.cwd()
        focus = self.resolve_path(preferred_image) if preferred_image is not None else None
        if focus is None and path.is_file():
            focus = path

        directory = path if path.is_dir() else path.parent
        request_target = str(focus or path)
        try:
            directory_key = str(directory.resolve())
        except OSError:
            directory_key = str(directory)

        if not self._ensure_directory_guard(directory_key, request_target):
            self.show_status(
                f"Directory is already open in another Coil Viewer window: {directory}"
            )
            return

        resolve_ms = (time.perf_counter() - total_start) * 1000
        list_start = time.perf_counter()
        if path.is_dir():
            self.folder = path
            self.files = image_files(path, self.config)
            self.index = 0
        else:
            self.folder = path.parent
            self.files = image_files(path.parent, self.config)
            if focus is not None and focus in self.files:
                self.index = self.files.index(focus)
            else:
                self.index = self.files.index(path) if path in self.files else 0
        list_ms = (time.perf_counter() - list_start) * 1000
        self.cache.clear()
        self.cache_is_scaled.clear()
        self._preload_queue.clear()
        self._preload_wanted.clear()
        self.loading_message = None
        log(
            f"[LOAD_START] path='{path}' files={len(self.files)} index={self.index} "
            f"resolve={resolve_ms:.1f}ms list={list_ms:.1f}ms total={(time.perf_counter() - total_start) * 1000:.1f}ms"
        )
        self._start_folder_monitor(self.folder)
        self.load_current()

    def _ensure_directory_guard(self, directory: str, request_path: str) -> bool:
        if self._directory_guard is not None and self._directory_guard.directory == directory:
            return True

        guard = DirectoryInstanceGuard.try_acquire(directory)
        if guard is None:
            DirectoryInstanceGuard.signal_existing(directory, request_path)
            return False

        self._release_directory_guard()
        self._directory_guard = guard
        guard.attach_window(self)
        guard.set_request_handler(self._on_external_open_request)
        return True

    def _release_directory_guard(self) -> None:
        if self._directory_guard is None:
            return
        self._directory_guard.set_request_handler(None)
        self._directory_guard.dispose()
        self._directory_guard = None

    def _on_external_open_request(self, target_path: str | None) -> None:
        if target_path:
            self.load_start(Path(target_path))

    def show_status(self, message: str) -> None:
        self._status_message = message
        self._status_seq += 1
        seq = self._status_seq
        self.queue_draw()
        GLib.timeout_add(2500, self._clear_status, seq)

    def _clear_status(self, seq: int) -> bool:
        if seq == self._status_seq:
            self._status_message = None
            self.queue_draw()
        return False

    def _trim_archive_history(self) -> None:
        overflow = len(self._archive_history) - MAX_ARCHIVE_HISTORY_SIZE
        if overflow > 0:
            del self._archive_history[:overflow]

    def _stop_folder_monitor(self) -> None:
        if self._folder_monitor is not None:
            self._folder_monitor.cancel()
            self._folder_monitor = None

    def _start_folder_monitor(self, folder: Path) -> None:
        self._stop_folder_monitor()
        try:
            gfile = Gio.File.new_for_path(str(folder))
            self._folder_monitor = gfile.monitor_directory(
                Gio.FileMonitorFlags.WATCH_MOVES,
                None,
            )
            self._folder_monitor.connect("changed", self._on_folder_changed)
            log(f"[FOLDERWATCH] watching '{folder}'")
        except Exception as exc:
            log_error(f"failed to watch folder {folder}: {exc}")

    def _on_folder_changed(self, _monitor, file, _other_file, event_type) -> None:
        relevant = {
            Gio.FileMonitorEvent.CREATED,
            Gio.FileMonitorEvent.DELETED,
            Gio.FileMonitorEvent.RENAMED,
            Gio.FileMonitorEvent.MOVED_IN,
            Gio.FileMonitorEvent.MOVED_OUT,
        }
        if event_type not in relevant:
            return

        path = Path(file.get_path() or "")
        if event_type != Gio.FileMonitorEvent.DELETED and path.suffix.lower() not in IMAGE_EXTS:
            return

        if self._rescan_debounce_id is not None:
            GLib.source_remove(self._rescan_debounce_id)
        self._rescan_debounce_id = GLib.timeout_add(
            FOLDER_RESCAN_DEBOUNCE_MS,
            self._rescan_folder_debounced,
        )

    def _rescan_folder_debounced(self) -> bool:
        self._rescan_debounce_id = None
        self.sync_folder_from_disk()
        return False

    def sync_folder_from_disk(self) -> None:
        if not self.folder.is_dir():
            return

        current = self.files[self.index] if self.files else None
        old_count = len(self.files)
        new_files = image_files(self.folder, self.config)
        new_set = set(new_files)

        for path in list(self.cache):
            if path not in new_set:
                del self.cache[path]
                self.cache_is_scaled.pop(path, None)

        self.files = new_files
        if not self.files:
            self.index = 0
            self.pixbuf = None
            self.set_title(APP_NAME)
            self.queue_draw()
            log("[FOLDER] folder empty after rescan")
            return

        if current is not None and current in self.files:
            self.index = self.files.index(current)
        else:
            self.index = min(self.index, len(self.files) - 1)

        added = len(self.files) - old_count
        if added > 0:
            log(f"[FOLDER] +{added} new image(s), total={len(self.files)}")
        elif len(self.files) != old_count:
            log(f"[FOLDER] resynced total={len(self.files)} (was {old_count})")

        if current is not None and current in self.files and self.pixbuf is not None:
            self.set_title(f"{APP_NAME} {VERSION} - {self.files[self.index].name}")
            self.queue_draw()
            GLib.idle_add(self.preload_neighbors)
        else:
            self.load_current()

    def load_pixbuf(self, path: Path, full_resolution: bool = False) -> GdkPixbuf.Pixbuf | None:
        cached = self.cache.get(path)
        cached_is_scaled = self.cache_is_scaled.get(path, False)
        if cached is not None and not (full_resolution and cached_is_scaled):
            tier = "scaled" if cached_is_scaled else "full"
            log(f"[PIXBUF] cache hit '{path.name}' tier={tier}")
            return cached
        start = time.perf_counter()
        try:
            pixbuf, scaled = self._load_pixbuf_for_view(path, full_resolution)
        except Exception as exc:
            log_error(f"failed to load {path}: {exc}")
            print(f"failed to load {path}: {exc}", file=sys.stderr)
            return None
        self.cache[path] = pixbuf
        self.cache_is_scaled[path] = scaled
        tier = "scaled" if scaled else "full"
        log(
            f"[PIXBUF] loaded '{path.name}' tier={tier} {pixbuf.get_width()}x{pixbuf.get_height()} "
            f"in {(time.perf_counter() - start) * 1000:.1f}ms"
        )
        return pixbuf

    def _display_target_size(self) -> tuple[int, int]:
        width = self.area.get_allocated_width()
        height = self.area.get_allocated_height()
        if width > 1 and height > 1:
            return width, height

        screen = Gdk.Screen.get_default()
        if screen is not None:
            return max(1, screen.get_width()), max(1, screen.get_height())

        return 1920, 1080

    def _load_pixbuf_for_view(
        self,
        path: Path,
        full_resolution: bool,
    ) -> tuple[GdkPixbuf.Pixbuf, bool]:
        if full_resolution:
            return GdkPixbuf.Pixbuf.new_from_file(str(path)), False

        target_w, target_h = self._display_target_size()
        try:
            _fmt, image_w, image_h = GdkPixbuf.Pixbuf.get_file_info(str(path))
        except Exception:
            image_w = image_h = 0

        if image_w > target_w * 1.5 or image_h > target_h * 1.5:
            pixbuf = GdkPixbuf.Pixbuf.new_from_file_at_scale(
                str(path), target_w, target_h, True
            )
            return pixbuf, True

        return GdkPixbuf.Pixbuf.new_from_file(str(path)), False

    def load_current(self) -> None:
        start = time.perf_counter()
        if not self.files:
            self.pixbuf = None
            self.set_title(APP_NAME)
            self.queue_draw()
            log("[LOAD_CURRENT] no images")
            return
        self.index %= len(self.files)
        self.zoom = 1.0
        self.pan_x = 0.0
        self.pan_y = 0.0
        self.pixbuf = self.load_pixbuf(self.files[self.index])
        self.set_title(f"{APP_NAME} {VERSION} - {self.files[self.index].name}")
        self.queue_draw()
        log(
            f"[LOAD_CURRENT] index={self.index + 1}/{len(self.files)} "
            f"name='{self.files[self.index].name}' total={(time.perf_counter() - start) * 1000:.1f}ms"
        )
        if not self._first_image_painted:
            startup_mark("current image decoded")
        GLib.idle_add(self.preload_neighbors)

    def preload_neighbors(self) -> bool:
        start = time.perf_counter()
        if not self.files:
            return False
        radius = max(0, int(self.config.preload_count))
        count = len(self.files)
        wanted: set[Path] = set()
        for delta in range(-radius, radius + 1):
            idx = self.index + delta
            if self.config.loop:
                idx %= count
            if 0 <= idx < count:
                wanted.add(self.files[idx])
        for path in list(self.cache):
            if path not in wanted:
                del self.cache[path]
                self.cache_is_scaled.pop(path, None)
        current = self.files[self.index]
        self._preload_wanted = wanted
        self._preload_queue = [
            path for path in wanted
            if path != current and path not in self.cache
        ]
        log(
            f"[PRELOAD] queued={len(self._preload_queue)} radius={radius} "
            f"setup={(time.perf_counter() - start) * 1000:.1f}ms"
        )
        if self._preload_queue and self._preload_source_id is None:
            self._preload_source_id = GLib.idle_add(self._preload_next)
        return False

    def _preload_next(self) -> bool:
        if not self._preload_queue:
            self._preload_source_id = None
            return False

        if self._preload_future is not None:
            self._preload_source_id = None
            return False

        path = self._preload_queue.pop(0)
        if path not in self.cache:
            self._preload_future = self._preload_executor.submit(self._load_preload_pixbuf, path)
            self._preload_future.add_done_callback(
                lambda future: GLib.idle_add(self._finish_preload, future)
            )
            self._preload_source_id = None
            return False

        if not self._preload_queue:
            self._preload_source_id = None
            log("[PRELOAD] complete")
            return False

        return True

    def _load_preload_pixbuf(self, path: Path) -> tuple[Path, GdkPixbuf.Pixbuf, bool] | None:
        start = time.perf_counter()
        try:
            pixbuf, scaled = self._load_pixbuf_for_view(path, full_resolution=False)
        except Exception as exc:
            log_error(f"failed to preload {path}: {exc}")
            return None

        tier = "scaled" if scaled else "full"
        log(
            f"[PRELOAD] decoded '{path.name}' tier={tier} "
            f"{pixbuf.get_width()}x{pixbuf.get_height()} in {(time.perf_counter() - start) * 1000:.1f}ms"
        )
        return path, pixbuf, scaled

    def _finish_preload(self, future: Future[tuple[Path, GdkPixbuf.Pixbuf, bool] | None]) -> bool:
        self._preload_future = None
        try:
            result = future.result()
        except Exception as exc:
            log_error(f"preload worker failed: {exc}")
            result = None

        if result is not None:
            path, pixbuf, scaled = result
            if path in self._preload_wanted:
                self.cache[path] = pixbuf
                self.cache_is_scaled[path] = scaled

        if self._preload_queue:
            self._preload_source_id = GLib.idle_add(self._preload_next)
        else:
            self._preload_source_id = None
            log("[PRELOAD] complete")

        return False

    # --- navigation --------------------------------------------------------
    def go(self, delta: int) -> None:
        if not self.files:
            return
        count = len(self.files)
        new_index = self.index + delta
        if self.config.loop:
            new_index %= count
        elif new_index < 0 or new_index >= count:
            # At a boundary with looping off: stick and flash the edge.
            self.flash_edge("start" if new_index < 0 else "end")
            return
        if new_index == self.index:
            return
        self.index = new_index
        self.load_current()

    def flash_edge(self, side: str) -> None:
        self.edge_flash = side
        self._flash_seq += 1
        seq = self._flash_seq
        self.queue_draw()
        GLib.timeout_add(400, self._clear_edge_flash, seq)

    def _clear_edge_flash(self, seq: int) -> bool:
        if seq == self._flash_seq:
            self.edge_flash = None
            self.queue_draw()
        return False

    def jump_fraction(self, direction: int) -> None:
        if not self.files:
            return
        target = 0 if direction < 0 else len(self.files) - 1
        self.index = self.index + ((target - self.index) // 2)
        self.load_current()

    def archive_current(self) -> None:
        if not self.files:
            self.show_status("No image to move.")
            return

        src = self.files[self.index]
        current_index = self.index
        has_next = current_index + 1 < len(self.files)

        try:
            old_dir = src.parent / "old"
            old_dir.mkdir(exist_ok=True)
            dst = archive_collision_path(old_dir, src)
        except OSError as exc:
            log_error(f"failed to prepare move for {src}: {exc}")
            self.show_status(f"Failed to move image '{src.name}': {exc}")
            return

        if has_next:
            self.index = current_index + 1

        self.cache.pop(src, None)
        self.cache_is_scaled.pop(src, None)
        self.files.pop(current_index)
        if not has_next and self.index >= len(self.files):
            self.index = max(0, len(self.files) - 1)

        self.zoom = 1.0
        self.pan_x = 0.0
        self.pan_y = 0.0
        if self.files:
            self.load_current()
        else:
            self.pixbuf = None
            self.set_title(APP_NAME)
            self.queue_draw()

        self._archive_executor.submit(self._move_to_old_async, src, dst)

    def _move_to_old_async(self, src: Path, dst: Path) -> None:
        try:
            shutil.move(str(src), str(dst))
        except OSError as exc:
            GLib.idle_add(self._on_archive_move_failed, src, exc)
            return
        GLib.idle_add(self._on_archive_move_complete, src, dst)

    def _on_archive_move_complete(self, src: Path, dst: Path) -> bool:
        self._archive_history.append(ArchiveStep(src, dst))
        self._trim_archive_history()
        log(f"[ARCHIVE] moved '{src}' to '{dst}'")
        self.show_status(f"Moved to '{dst}'")
        return False

    def _on_archive_move_failed(self, src: Path, exc: OSError) -> bool:
        log_error(f"failed to move {src}: {exc}")
        self.show_status(f"Failed to move image '{src.name}': {exc}")
        return False

    def undo_archive(self) -> None:
        if not self._archive_history:
            self.show_status("Nothing to undo.")
            return

        action = self._archive_history.pop()
        try:
            if not action.archived_path.exists():
                self.show_status(f"Cannot undo: '{action.archived_path}' is missing.")
                return

            destination_directory = action.original_path.parent
            if not destination_directory:
                self.show_status("Cannot undo: destination is invalid.")
                return

            destination_directory.mkdir(parents=True, exist_ok=True)
            if action.original_path.exists():
                self.show_status(f"Cannot undo: '{action.original_path}' already exists.")
                self._archive_history.append(action)
                return

            shutil.move(str(action.archived_path), str(action.original_path))
            log(f"[ARCHIVE] restored '{action.original_path}' from '{action.archived_path}'")
            self.load_start(action.original_path, action.original_path)
            self.show_status(f"Restored to '{action.original_path}'.")
        except OSError as exc:
            self._archive_history.append(action)
            log_error(
                f"failed to undo archive for '{action.original_path}' "
                f"from '{action.archived_path}': {exc}"
            )
            self.show_status("Failed to undo archive.")

    # --- config / sort -----------------------------------------------------
    def save_config(self) -> None:
        try:
            CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
            CONFIG_PATH.write_text(json.dumps(asdict(self.config), indent=2), encoding="utf-8")
        except Exception as exc:
            log_error(f"failed to save config: {exc}")
            print(f"failed to save config: {exc}", file=sys.stderr)

    def reload_config(self) -> None:
        current = self.files[self.index] if self.files else None
        self.config = load_config()
        self.cache.clear()
        self.cache_is_scaled.clear()
        self.files = image_files(self.folder, self.config)
        if current is not None and current in self.files:
            self.index = self.files.index(current)
        else:
            self.index = min(self.index, max(0, len(self.files) - 1))
        self.load_current()

    def apply_sort(self, field: str, direction: str) -> None:
        current = self.files[self.index] if self.files else None
        self.config.sort_field = field
        self.config.sort_direction = direction
        self.save_config()
        self.files = image_files(self.folder, self.config)
        if current is not None and current in self.files:
            self.index = self.files.index(current)
        else:
            self.index = 0
        self.load_current()

    # --- input -------------------------------------------------------------
    def on_key(self, _widget, event) -> bool:
        key = Gdk.keyval_name(event.keyval) or ""
        ctrl = bool(event.state & Gdk.ModifierType.CONTROL_MASK)
        shift = bool(event.state & Gdk.ModifierType.SHIFT_MASK)

        arrows = {"Right", "Left", "Up", "Down"}

        if key in {"Escape", "q"}:
            Gtk.main_quit()
        elif key in {"Right", "Down", "space"} and ctrl and shift:
            self.jump_fraction(1)
        elif key in {"Left", "Up", "BackSpace"} and ctrl and shift:
            self.jump_fraction(-1)
        elif key in {"c", "C"} and ctrl:
            self.copy_image_to_clipboard()
        elif key in {"r", "R"} and ctrl:
            self.reload_config()
        elif key in {"o", "O"} and ctrl:
            self.prompt_open()
        elif (key in {"s", "S"} and ctrl and not shift) or (key == "comma" and ctrl):
            self.open_settings_dialog()
        elif key in arrows and self.is_zoomed():
            if key == "Right":
                self.pan_x -= PAN_STEP
            elif key == "Left":
                self.pan_x += PAN_STEP
            elif key == "Up":
                self.pan_y += PAN_STEP
            elif key == "Down":
                self.pan_y -= PAN_STEP
            self.queue_draw()
        elif key in {"Right", "Down", "space"}:
            self.go(1)
        elif key in {"Left", "Up", "BackSpace"}:
            self.go(-1)
        elif key == "Home":
            if self.files and self.index != 0:
                self.index = 0
                self.load_current()
        elif key == "End":
            if self.files and self.index != len(self.files) - 1:
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
        elif (key == "z" and ctrl) or key in {"u", "U"}:
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

    def is_zoomed(self) -> bool:
        return self.zoom > 1.01

    def zoom_at_center(self, factor: float) -> None:
        self.zoom = max(0.1, min(20.0, self.zoom * factor))
        if self.zoom > 1.01 and self.files:
            current = self.files[self.index]
            if self.cache_is_scaled.get(current, False):
                full_pixbuf = self.load_pixbuf(current, full_resolution=True)
                if full_pixbuf is not None:
                    self.pixbuf = full_pixbuf
        self.queue_draw()

    def on_scroll(self, _widget, event) -> bool:
        if self.is_zoomed():
            if event.direction == Gdk.ScrollDirection.UP:
                self.pan_y += PAN_STEP
            elif event.direction == Gdk.ScrollDirection.DOWN:
                self.pan_y -= PAN_STEP
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
        if event.button == 3:
            self.show_context_menu(event)
            return True
        if event.button == 1:
            self.drag_start = (event.x - self.pan_x, event.y - self.pan_y)
        return True

    def on_button_release(self, *_args) -> bool:
        self.drag_start = None
        return True

    def on_motion(self, _widget, event) -> bool:
        if self.drag_start and self.is_zoomed():
            self.pan_x = event.x - self.drag_start[0]
            self.pan_y = event.y - self.drag_start[1]
            self.queue_draw()
        return True

    def on_drag_data_received(self, _widget, _context, _x, _y, data, _info, _time) -> None:
        uris = data.get_uris()
        if uris:
            path = Path(GLib.filename_from_uri(uris[0])[0])
            self.load_start(path)

    # --- context menu ------------------------------------------------------
    def show_context_menu(self, event) -> None:
        menu = Gtk.Menu()
        has_images = bool(self.files)
        current = self.files[self.index] if has_images else None

        copy_path_item = Gtk.MenuItem(label="Copy full path")
        copy_path_item.set_sensitive(has_images)
        copy_path_item.connect("activate", self.on_copy_full_path)
        menu.append(copy_path_item)

        copy_image_item = Gtk.MenuItem(label="Copy image")
        copy_image_item.set_sensitive(self.pixbuf is not None)
        copy_image_item.connect("activate", lambda _i: self.copy_image_to_clipboard())
        menu.append(copy_image_item)

        if has_images:
            info_label = f"Image: {self.index + 1}/{len(self.files)} ({current.name})"
        else:
            info_label = "Image: --/--"
        info_item = Gtk.MenuItem(label=info_label)
        info_item.set_sensitive(False)
        menu.append(info_item)

        field_label = SORT_FIELD_LABELS.get(self.config.sort_field, self.config.sort_field)
        dir_label = "ASC" if self.config.sort_direction == "ascending" else "DESC"
        sort_status = Gtk.MenuItem(label=f"Sort: {field_label} {dir_label}")
        sort_status.set_sensitive(False)
        menu.append(sort_status)

        menu.append(Gtk.SeparatorMenuItem())

        for label, field in SORT_MENU_FIELDS:
            for arrow, direction in (("\u2191", "ascending"), ("\u2193", "descending")):
                item = Gtk.CheckMenuItem(label=f"{label} {arrow}")
                item.set_draw_as_radio(True)
                item.set_active(
                    self.config.sort_field == field
                    and self.config.sort_direction == direction
                )
                item.connect("activate", self.on_sort_selected, field, direction)
                menu.append(item)

        menu.append(Gtk.SeparatorMenuItem())

        open_item = Gtk.MenuItem(label="Open...")
        open_item.connect("activate", lambda _i: self.prompt_open())
        menu.append(open_item)

        settings_item = Gtk.MenuItem(label="Settings...")
        settings_item.connect("activate", lambda _i: self.open_settings_dialog())
        menu.append(settings_item)

        menu.show_all()
        # Keep a reference so the menu is not garbage-collected while shown.
        self.context_menu = menu
        menu.popup_at_pointer(event)

    def on_copy_full_path(self, _item) -> None:
        if not self.files:
            return
        clipboard = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
        clipboard.set_text(str(self.files[self.index]), -1)
        clipboard.store()

    def copy_image_to_clipboard(self) -> None:
        if self.pixbuf is None:
            return
        if self.files:
            current = self.files[self.index]
            if self.cache_is_scaled.get(current, False):
                full_pixbuf = self.load_pixbuf(current, full_resolution=True)
                if full_pixbuf is not None:
                    self.pixbuf = full_pixbuf
        clipboard = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
        clipboard.set_image(self.pixbuf)
        clipboard.store()

    def on_sort_selected(self, _item, field: str, direction: str) -> None:
        self.apply_sort(field, direction)

    # --- dialogs -----------------------------------------------------------
    def prompt_open(self) -> None:
        dialog = Gtk.FileChooserDialog(
            title="Open image",
            transient_for=self,
            action=Gtk.FileChooserAction.OPEN,
        )
        dialog.add_buttons(
            "Cancel", Gtk.ResponseType.CANCEL,
            "Open", Gtk.ResponseType.OK,
        )
        image_filter = Gtk.FileFilter()
        image_filter.set_name("Images")
        for ext in sorted(IMAGE_EXTS):
            image_filter.add_pattern(f"*{ext}")
        dialog.add_filter(image_filter)
        all_filter = Gtk.FileFilter()
        all_filter.set_name("All files")
        all_filter.add_pattern("*")
        dialog.add_filter(all_filter)
        try:
            if self.folder.is_dir():
                dialog.set_current_folder(str(self.folder))
        except Exception:
            pass
        if dialog.run() == Gtk.ResponseType.OK:
            filename = dialog.get_filename()
            if filename:
                self.load_start(Path(filename))
        dialog.destroy()

    def open_settings_dialog(self) -> None:
        dialog = Gtk.Dialog(title="Settings", transient_for=self, modal=True)
        dialog.add_buttons(
            "Cancel", Gtk.ResponseType.CANCEL,
            "OK", Gtk.ResponseType.OK,
        )
        content = dialog.get_content_area()
        content.set_spacing(12)
        content.set_border_width(18)

        preload_row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        preload_row.pack_start(Gtk.Label(label="Preload image count:"), False, False, 0)
        adjustment = Gtk.Adjustment(
            value=self.config.preload_count, lower=0, upper=200, step_increment=1, page_increment=10
        )
        preload_spin = Gtk.SpinButton(adjustment=adjustment, climb_rate=1, digits=0)
        preload_row.pack_start(preload_spin, False, False, 0)
        content.add(preload_row)

        overlay_check = Gtk.CheckButton(label="Show overlay")
        overlay_check.set_active(self.config.show_overlay)
        content.add(overlay_check)

        loop_check = Gtk.CheckButton(label="Loop around")
        loop_check.set_active(self.config.loop)
        content.add(loop_check)

        fill_check = Gtk.CheckButton(label="Fill window (crop to fit)")
        fill_check.set_active(self.config.fit_mode.lower() == "uniformtofill")
        content.add(fill_check)

        dialog.show_all()
        if dialog.run() == Gtk.ResponseType.OK:
            self.config.preload_count = int(preload_spin.get_value())
            self.config.show_overlay = overlay_check.get_active()
            self.config.loop = loop_check.get_active()
            self.config.fit_mode = "UniformToFill" if fill_check.get_active() else "Uniform"
            self.save_config()
            self.cache.clear()
            self.cache_is_scaled.clear()
            GLib.idle_add(self.preload_neighbors)
            self.queue_draw()
        dialog.destroy()

    # --- drawing -----------------------------------------------------------
    def on_draw(self, widget, cr) -> bool:
        width = widget.get_allocated_width()
        height = widget.get_allocated_height()
        bg = Gdk.RGBA()
        if not bg.parse(self.config.background):
            bg.parse("#000000")
        cr.set_source_rgba(bg.red, bg.green, bg.blue, bg.alpha)
        cr.rectangle(0, 0, width, height)
        cr.fill()

        if self.pixbuf:
            iw = self.pixbuf.get_width()
            ih = self.pixbuf.get_height()
            if self.config.fit_mode.lower() == "uniformtofill":
                base_scale = max(width / iw, height / ih)
            else:
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
            try:
                pattern = cr.get_source()
                fast = self.config.scaling_mode.lower() == "fast"
                pattern.set_filter(cairo.FILTER_FAST if fast else cairo.FILTER_GOOD)
            except Exception:
                pass
            cr.paint()
            cr.restore()
            if not self._first_image_painted:
                self._first_image_painted = True
                startup_mark("first image painted")
        else:
            self.draw_text(cr, self.loading_message or "No images found", 40, 60, 28)

        if self.config.show_overlay:
            self.draw_overlay(cr, width, height)
        if self.edge_flash:
            self.draw_edge_flash(cr, width, height)
        if self.show_help:
            self.draw_help(cr, width, height)
        if self._status_message:
            self.draw_status(cr, width, height)
        return False

    def draw_edge_flash(self, cr, width: int, height: int) -> None:
        bar_w = 10
        x = 0 if self.edge_flash == "start" else width - bar_w
        cr.set_source_rgba(1, 1, 1, 0.5)
        cr.rectangle(x, 0, bar_w, height)
        cr.fill()

    def draw_overlay(self, cr, width: int, _height: int) -> None:
        if not self.files:
            return
        path = self.files[self.index]
        parts: list[str] = []
        if self.pixbuf:
            parts.append(f"{self.pixbuf.get_width()}x{self.pixbuf.get_height()}")
        try:
            parts.append(format_size(path.stat().st_size))
        except OSError:
            pass
        parts.append(f"{self.index + 1}/{len(self.files)}")
        parts.append(path.name)
        field_label = SORT_FIELD_LABELS.get(self.config.sort_field, self.config.sort_field)
        dir_label = "ASC" if self.config.sort_direction == "ascending" else "DESC"
        parts.append(f"sort={field_label} {dir_label}")
        text = "   ".join(parts)
        cr.set_source_rgba(0, 0, 0, 0.65)
        cr.rectangle(0, 0, width, 34)
        cr.fill()
        self.draw_text(cr, text, 12, 23, 16)

    def draw_status(self, cr, width: int, height: int) -> None:
        if not self._status_message:
            return
        box_h = 40
        y = height - box_h - 16
        cr.set_source_rgba(0, 0, 0, 0.82)
        cr.rectangle(16, y, width - 32, box_h)
        cr.fill()
        self.draw_text(cr, self._status_message, 28, y + 26, 16)

    def draw_help(self, cr, width: int, height: int) -> None:
        lines = [
            "Right/Space: next    Left/Backspace: previous",
            "Home/End: first/last    Ctrl+Shift+Arrow: half jump",
            "+/-: zoom    \\: reset zoom    arrows/wheel: pan when zoomed, else navigate",
            "A: archive to old    Ctrl+Z or U: undo archive",
            "Ctrl+O: open    Ctrl+R: reload config    Ctrl+C: copy image",
            "Right-click: menu (copy path/image, sort, open, settings)",
            "I: overlay    /: help    F11/double-click: fullscreen    Esc/Q: quit",
        ]
        box_w = min(880, width - 80)
        box_h = 38 + len(lines) * 28 + 16
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


def try_early_redirect(start: Path | None) -> bool:
    if start is None:
        return False

    expanded = os.path.expandvars(str(start))
    candidate = Path(expanded).expanduser()
    try:
        resolved = candidate.resolve()
    except OSError:
        resolved = candidate

    directory = resolve_directory(resolved)
    if directory is None:
        return False

    return DirectoryInstanceGuard.try_redirect_to_existing_instance(str(directory), str(resolved))


def main() -> int:
    startup_mark("main entered")
    start = Path(sys.argv[1]) if len(sys.argv) > 1 else None
    if try_early_redirect(start):
        log("[STARTUP] early redirect exit (existing instance)")
        return 0

    ok, _ = Gtk.init_check(sys.argv)
    if not ok:
        print("Unable to initialize GTK; is DISPLAY available?", file=sys.stderr)
        return 1
    startup_mark("Gtk.init_check complete")
    log(f"startup {APP_NAME} {VERSION} arg={sys.argv[1] if len(sys.argv) > 1 else ''}")
    window = CoilBrowser(start)
    startup_mark("window object constructed")
    window.show_all()
    startup_mark("window.show_all returned")
    Gtk.main()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
