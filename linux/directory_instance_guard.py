"""One open CoilViewer window per image directory (Linux port of DirectoryInstanceGuard)."""

from __future__ import annotations

import fcntl
import hashlib
import os
import socket
import threading
import time
from pathlib import Path
from typing import Callable

from gi.repository import GLib

LOCK_DIR = Path.home() / ".local" / "share" / "coilbrowser" / "instance-locks"
LOG_DIR = Path.home() / ".local" / "share" / "coilbrowser"


def _log(filename: str, message: str) -> None:
    try:
        LOG_DIR.mkdir(parents=True, exist_ok=True)
        stamp = time.strftime("%Y-%m-%d %H:%M:%S")
        with (LOG_DIR / filename).open("a", encoding="utf-8") as handle:
            handle.write(f"{stamp} {message}\n")
    except OSError:
        pass


def log(message: str) -> None:
    _log("coilbrowser-launch.log", message)


def log_error(message: str) -> None:
    _log("coilbrowser-errors.log", message)

LOCK_DIR = Path.home() / ".local" / "share" / "coilbrowser" / "instance-locks"
SOCKET_DIR = Path.home() / ".local" / "share" / "coilbrowser" / "instance-sockets"


def compute_hash(directory: str) -> str:
    return hashlib.sha256(directory.upper().encode("utf-8")).hexdigest()


def resolve_directory(path: str | Path | None) -> Path | None:
    if path is None or not str(path).strip():
        return None

    try:
        full = Path(path).expanduser().resolve()
        if full.is_dir():
            return full
        if full.is_file():
            return full.parent.resolve()
    except OSError:
        pass

    return None


def _lock_path(directory: str) -> Path:
    return LOCK_DIR / f"{compute_hash(directory)}.lock"


def _socket_path(directory: str) -> Path:
    short = compute_hash(directory)[:32]
    runtime = os.environ.get("XDG_RUNTIME_DIR")
    if runtime:
        return Path(runtime) / f"coilviewer-{short}.sock"
    return Path("/tmp") / f"coilviewer-{short}.sock"


def _send_open_request(directory: str, requested_path: str | None) -> bool:
    sock_path = _socket_path(directory)
    if not sock_path.exists():
        return False

    try:
        client = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        client.settimeout(0.5)
        client.connect(str(sock_path))
        payload = (requested_path or "").strip() + "\n"
        client.sendall(payload.encode("utf-8"))
        client.close()
        log(f"[INSTANCE] open request sent for '{directory}' target='{requested_path}'")
        return True
    except OSError as exc:
        log_error(f"failed to send open request for '{directory}': {exc}")
        return False


class DirectoryInstanceGuard:
    def __init__(
        self,
        directory: str,
        lock_handle,
        server: socket.socket,
        listener: threading.Thread,
        stop_event: threading.Event,
    ) -> None:
        self.directory = directory
        self._lock_handle = lock_handle
        self._server = server
        self._listener = listener
        self._stop_event = stop_event
        self._request_handler: Callable[[str | None], None] | None = None
        self._window = None

    @staticmethod
    def try_acquire(directory: str) -> DirectoryInstanceGuard | None:
        LOCK_DIR.mkdir(parents=True, exist_ok=True)

        lock_path = _lock_path(directory)
        try:
            lock_handle = open(lock_path, "a+", encoding="utf-8")
            fcntl.flock(lock_handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except OSError:
            return None

        sock_path = _socket_path(directory)
        if sock_path.exists():
            try:
                sock_path.unlink()
            except OSError:
                pass

        server = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        server.bind(str(sock_path))
        server.listen(1)
        server.settimeout(0.5)

        stop_event = threading.Event()
        guard = DirectoryInstanceGuard(directory, lock_handle, server, None, stop_event)  # type: ignore[arg-type]
        listener = threading.Thread(
            target=guard._listen_for_requests,
            name=f"coil-instance-{compute_hash(directory)[:8]}",
            daemon=True,
        )
        guard._listener = listener
        listener.start()
        log(f"[INSTANCE] acquired guard for '{directory}'")
        return guard

    @staticmethod
    def is_directory_already_open(directory: str) -> bool:
        LOCK_DIR.mkdir(parents=True, exist_ok=True)
        lock_path = _lock_path(directory)
        try:
            handle = open(lock_path, "a+", encoding="utf-8")
            try:
                fcntl.flock(handle.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                fcntl.flock(handle.fileno(), fcntl.LOCK_UN)
                return False
            finally:
                handle.close()
        except OSError:
            return True

    @staticmethod
    def signal_existing(directory: str, requested_path: str | None) -> bool:
        return _send_open_request(directory, requested_path)

    @staticmethod
    def try_redirect_to_existing_instance(directory: str, requested_path: str | None) -> bool:
        if not DirectoryInstanceGuard.is_directory_already_open(directory):
            log(f"[STARTUP] early redirect: no existing instance for '{directory}'")
            return False

        ok = DirectoryInstanceGuard.signal_existing(directory, requested_path)
        log(f"[STARTUP] early redirect signal result={ok} for '{directory}'")
        return ok

    def attach_window(self, window) -> None:
        self._window = window

    def set_request_handler(self, handler: Callable[[str | None], None] | None) -> None:
        self._request_handler = handler

    def dispose(self) -> None:
        self._stop_event.set()
        try:
            self._server.close()
        except OSError:
            pass

        if self._listener is not None:
            self._listener.join(timeout=1.0)

        sock_path = _socket_path(self.directory)
        if sock_path.exists():
            try:
                sock_path.unlink()
            except OSError:
                pass

        try:
            fcntl.flock(self._lock_handle.fileno(), fcntl.LOCK_UN)
        except OSError:
            pass

        try:
            self._lock_handle.close()
        except OSError:
            pass

        self._window = None
        self._request_handler = None
        log(f"[INSTANCE] released guard for '{self.directory}'")

    def _listen_for_requests(self) -> None:
        while not self._stop_event.is_set():
            try:
                conn, _addr = self._server.accept()
            except socket.timeout:
                continue
            except OSError:
                if self._stop_event.is_set():
                    break
                continue

            message = ""
            try:
                with conn:
                    chunks: list[bytes] = []
                    while True:
                        data = conn.recv(4096)
                        if not data:
                            break
                        chunks.append(data)
                    message = b"".join(chunks).decode("utf-8", errors="replace").strip()
            except OSError as exc:
                log_error(f"instance socket read failed for '{self.directory}': {exc}")
                continue

            log(f"[INSTANCE] received open request for '{self.directory}' target='{message}'")
            GLib.idle_add(self._dispatch_request, message or None)

    def _dispatch_request(self, message: str | None) -> bool:
        try:
            if self._request_handler is not None:
                self._request_handler(message)
            elif self._window is not None:
                self._present_window()
        except Exception as exc:
            log_error(f"failed to process open request for '{self.directory}': {exc}")
        finally:
            if self._window is not None:
                self._present_window()
        return False

    def _present_window(self) -> None:
        window = self._window
        if window is None:
            return
        try:
            window.present()
            window.set_keep_above(True)
            window.set_keep_above(False)
        except Exception as exc:
            log_error(f"failed to present window for '{self.directory}': {exc}")
