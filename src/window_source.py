"""Read-only native window discovery. Includes untitled tool windows."""
import ctypes as C
from ctypes import wintypes as W
from dataclasses import dataclass
from pathlib import Path

user32 = C.WinDLL("user32", use_last_error=True)
CALLBACK = C.WINFUNCTYPE(W.BOOL, W.HWND, W.LPARAM)
user32.EnumWindows.argtypes = [CALLBACK, W.LPARAM]
user32.GetWindowThreadProcessId.argtypes = [W.HWND, C.POINTER(W.DWORD)]
user32.IsWindowVisible.argtypes = [W.HWND]
user32.IsIconic.argtypes = [W.HWND]
user32.IsWindow.argtypes = [W.HWND]
user32.GetWindowRect.argtypes = [W.HWND, C.POINTER(W.RECT)]
user32.GetWindowTextW.argtypes = [W.HWND, W.LPWSTR, C.c_int]
user32.GetClassNameW.argtypes = [W.HWND, W.LPWSTR, C.c_int]
user32.GetWindowLongPtrW.argtypes = [W.HWND, C.c_int]
user32.GetWindowLongPtrW.restype = C.c_ssize_t
kernel32 = C.WinDLL("kernel32", use_last_error=True)
kernel32.OpenProcess.argtypes = [W.DWORD, W.BOOL, W.DWORD]
kernel32.OpenProcess.restype = W.HANDLE
kernel32.QueryFullProcessImageNameW.argtypes = [W.HANDLE, W.DWORD, W.LPWSTR, C.POINTER(W.DWORD)]
kernel32.CloseHandle.argtypes = [W.HANDLE]


def process_executable(pid):
    """Query only the image path; full process enumeration can omit GUI owners."""
    handle = kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not handle:
        return None
    try:
        buffer = C.create_unicode_buffer(32768)
        size = W.DWORD(len(buffer))
        if kernel32.QueryFullProcessImageNameW(handle, 0, buffer, C.byref(size)):
            return Path(buffer.value)
        return None
    finally:
        kernel32.CloseHandle(handle)


def doubao_window_processes():
    """Find Chromium GUI owners even when the client has only hidden windows."""
    paths, found = {}, {}

    @CALLBACK
    def visit(hwnd, _):
        cls = C.create_unicode_buffer(256)
        user32.GetClassNameW(hwnd, cls, len(cls))
        if not cls.value.startswith('Chrome_WidgetWin_'):
            return True
        pid = W.DWORD()
        user32.GetWindowThreadProcessId(hwnd, C.byref(pid))
        if pid.value not in paths:
            paths[pid.value] = process_executable(pid.value)
        path = paths[pid.value]
        if path and path.name.lower() == 'doubao.exe':
            found[pid.value] = path
        return True

    user32.EnumWindows(visit, 0)
    return found


@dataclass(frozen=True)
class Window:
    hwnd: int
    pid: int
    title: str
    class_name: str
    width: int
    height: int
    ex_style: int

    @property
    def label(self):
        return f"{self.title or '无标题字幕候选'} · {self.width}×{self.height} · HWND {self.hwnd}"

    @property
    def score(self):
        # Broad enough for resized captions; square assistant buttons score poorly.
        ratio = self.width / max(1, self.height)
        expanded = (not self.title and self.class_name == "Chrome_WidgetWin_1"
                    and self.ex_style & 0x80 and self.width >= 600 and 0.7 <= ratio <= 2.5)
        return (80 if not self.title else 0) + (40 if self.ex_style & 0x80 else 0) + (40 if ratio > 2.5 or expanded else 0) + (20 if self.width >= 350 else -100)


def doubao_windows():
    paths, found = {}, []

    @CALLBACK
    def visit(hwnd, _):
        pid = W.DWORD()
        if not user32.IsWindowVisible(hwnd) or user32.IsIconic(hwnd):
            return True
        user32.GetWindowThreadProcessId(hwnd, C.byref(pid))
        if pid.value not in paths:
            paths[pid.value] = process_executable(pid.value)
        path = paths[pid.value]
        if not path or path.name.lower() != 'doubao.exe':
            return True
        rect = W.RECT()
        if not user32.GetWindowRect(hwnd, C.byref(rect)):
            return True
        width, height = rect.right - rect.left, rect.bottom - rect.top
        if width < 80 or height < 40:
            return True
        title, cls = C.create_unicode_buffer(1024), C.create_unicode_buffer(256)
        user32.GetWindowTextW(hwnd, title, len(title))
        user32.GetClassNameW(hwnd, cls, len(cls))
        found.append(Window(int(hwnd), pid.value, title.value, cls.value, width, height, user32.GetWindowLongPtrW(hwnd, -20)))
        return True

    user32.EnumWindows(visit, 0)
    return sorted(found, key=lambda w: w.score, reverse=True)


def still_same_window(window):
    pid = W.DWORD()
    user32.GetWindowThreadProcessId(window.hwnd, C.byref(pid))
    return bool(user32.IsWindow(window.hwnd)) and pid.value == window.pid
