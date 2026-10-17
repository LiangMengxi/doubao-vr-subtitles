"""Read-only native window discovery. Includes untitled tool windows."""
import ctypes as C
from ctypes import wintypes as W
from dataclasses import dataclass
import psutil

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
    pids = set()
    for proc in psutil.process_iter(["pid", "name"]):
        if (proc.info["name"] or "").lower() == "doubao.exe":
            pids.add(proc.info["pid"])
    found = []

    @CALLBACK
    def visit(hwnd, _):
        pid = W.DWORD()
        user32.GetWindowThreadProcessId(hwnd, C.byref(pid))
        if pid.value not in pids or not user32.IsWindowVisible(hwnd) or user32.IsIconic(hwnd):
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
