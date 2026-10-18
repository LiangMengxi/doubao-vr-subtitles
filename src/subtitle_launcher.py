"""Launch Doubao's caption feature through its configured global shortcut.

Does not edit Doubao's settings or automate its menus. Poll from the UI thread;
each step is bounded, and a shortcut is sent at most once per button click.
"""
import ctypes as C
from ctypes import wintypes as W
import json
import logging
import os
from pathlib import Path
import subprocess
import time
import winreg

import psutil
from app_paths import RESOURCE_DIR
from window_source import doubao_windows, doubao_window_processes, still_same_window, user32


def caption_candidate(windows):
    return next((w for w in windows if w.score >= 150), None)


def read_caption_shortcut():
    path = Path(os.environ["LOCALAPPDATA"]) / "Doubao" / "User Data" / "Local State"
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        setting = data.get("saman.shortcut.audio_translate")
        if setting is None:
            setting = data.get("saman", {}).get("shortcut", {}).get("audio_translate")
        if not isinstance(setting, dict) or setting.get("enabled") is False:
            raise ValueError("实时字幕快捷键未配置或已禁用")
        key = setting.get("key", "")
        if not isinstance(key, str) or not key.strip():
            raise ValueError("实时字幕快捷键为空")
        parse_shortcut(key)  # Validate before any input is sent.
        return key
    except (OSError, ValueError, TypeError, AttributeError) as exc:
        raise RuntimeError(f"无法读取实时字幕快捷键：{exc}。请在豆包快捷键设置中配置“实时字幕”。") from exc


def parse_shortcut(shortcut):
    tokens = [token.strip().upper() for token in shortcut.split("+")]
    if not tokens or len(tokens) > 4:
        raise ValueError("不支持的快捷键")
    modifiers = {"ALT": 0x12, "CTRL": 0x11, "CONTROL": 0x11, "SHIFT": 0x10}
    keys = []
    for token in tokens[:-1]:
        if token not in modifiers or modifiers[token] in keys:
            raise ValueError("快捷键修饰键不受支持")
        keys.append(modifiers[token])
    key = tokens[-1]
    if len(key) == 1 and ("A" <= key <= "Z" or "0" <= key <= "9"):
        vk = ord(key)
    elif key.startswith("F") and key[1:].isdigit() and 1 <= int(key[1:]) <= 24:
        vk = 0x70 + int(key[1:]) - 1
    else:
        special = {"SPACE": 0x20, "ENTER": 0x0D, "RETURN": 0x0D, "TAB": 0x09,
                   "HOME": 0x24, "END": 0x23, "INSERT": 0x2D, "DELETE": 0x2E,
                   "PAGEUP": 0x21, "PAGEDOWN": 0x22}
        if key not in special:
            raise ValueError("当前快捷键无法自动触发，支持 Ctrl、Alt、Shift 与字母、数字或功能键组合")
        vk = special[key]
    if not keys:
        raise ValueError("请为实时字幕配置带 Ctrl、Alt 或 Shift 的组合键")
    return keys + [vk]


class KEYBDINPUT(C.Structure):
    _fields_ = [("wVk", W.WORD), ("wScan", W.WORD), ("dwFlags", W.DWORD),
                ("time", W.DWORD), ("dwExtraInfo", C.c_size_t)]


class MOUSEINPUT(C.Structure):
    _fields_ = [("dx", W.LONG), ("dy", W.LONG), ("mouseData", W.DWORD),
                ("dwFlags", W.DWORD), ("time", W.DWORD), ("dwExtraInfo", C.c_size_t)]


class HARDWAREINPUT(C.Structure):
    _fields_ = [("uMsg", W.DWORD), ("wParamL", W.WORD), ("wParamH", W.WORD)]


class INPUT_UNION(C.Union):
    _fields_ = [("ki", KEYBDINPUT), ("mi", MOUSEINPUT), ("hi", HARDWAREINPUT)]


class INPUT(C.Structure):
    _anonymous_ = ("data",)
    _fields_ = [("type", W.DWORD), ("data", INPUT_UNION)]


def send_shortcut(shortcut):
    keys = parse_shortcut(shortcut)
    api = C.WinDLL("user32", use_last_error=True)
    api.SendInput.argtypes = [W.UINT, C.POINTER(INPUT), C.c_int]
    api.SendInput.restype = W.UINT
    api.GetAsyncKeyState.argtypes = [C.c_int]
    api.GetAsyncKeyState.restype = C.c_short
    # Defer until the user releases keyboard modifiers, so the synthetic chord
    # cannot accidentally combine with a physically held Ctrl/Alt/Shift/Win.
    if any(api.GetAsyncKeyState(key) & 0x8000 for key in (0x10, 0x11, 0x12, 0x5B, 0x5C)):
        return False
    events = [INPUT(type=1, ki=KEYBDINPUT(wVk=key)) for key in keys]
    events += [INPUT(type=1, ki=KEYBDINPUT(wVk=key, dwFlags=2)) for key in reversed(keys)]
    inputs = (INPUT * len(events))(*events)
    if api.SendInput(len(inputs), inputs, C.sizeof(INPUT)) != len(inputs):
        # Best effort release of synthetic keys if Windows accepted a partial batch.
        releases = (INPUT * len(keys))(*(INPUT(type=1, ki=KEYBDINPUT(wVk=key, dwFlags=2)) for key in reversed(keys)))
        api.SendInput(len(releases), releases, C.sizeof(INPUT))
        raise RuntimeError("Windows 未能发送字幕快捷键，请确认豆包与本程序以相同权限运行。")
    return True


def running_doubao_exe():
    for executable in doubao_window_processes().values():
        return executable
    for proc in psutil.process_iter(["name", "exe"]):
        if (proc.info["name"] or "").lower() == "doubao.exe":
            path = proc.info["exe"]
            if path and Path(path).is_file():
                return Path(path)
    return None


def installed_doubao_exe():
    # The app's own registered protocol includes its real installation path,
    # including custom install drives. Never execute the registry command string.
    try:
        with winreg.OpenKey(winreg.HKEY_CLASSES_ROOT, r"doubaoapp\shell\open\command") as key:
            command = winreg.QueryValue(key, None).strip()
        if command.startswith('"'):
            path = Path(command.split('"', 2)[1])
        else:
            path = Path(command.split(" ", 1)[0])
        if path.name.lower() == "doubao.exe" and path.is_file():
            return path
    except OSError:
        pass
    for path in [Path(os.environ["LOCALAPPDATA"]) / "Doubao" / "Application" / "Doubao.exe"]:
        if path.is_file():
            return path
    raise RuntimeError("未找到豆包桌面版，请先安装并登录豆包。")


class SubtitleLauncher:
    def __init__(self):
        self.started = time.monotonic()
        self.deadline = self.started + 20
        self.sent = False
        self.key = None
        self.spawned = False
        self.existing = caption_candidate(doubao_windows())
        if self.existing:
            return
        if running_doubao_exe() is None:
            executable = installed_doubao_exe()
            startup = subprocess.STARTUPINFO()
            startup.dwFlags |= subprocess.STARTF_USESHOWWINDOW
            startup.wShowWindow = 0  # SW_HIDE: request a background GUI startup.
            # Use the installed client's auto-run entry instead of a normal
            # double-click launch, which opens the chat window. No browser URI.
            subprocess.Popen([str(executable), "--launch_from=auto_run", "--no-startup-window",
                              "--auto_launch_delay_seconds=0"],
                             cwd=str(executable.parent), startupinfo=startup,
                             creationflags=subprocess.CREATE_NO_WINDOW)
            self.spawned = True

    def poll(self):
        windows = doubao_windows()
        caption = caption_candidate(windows)
        if caption:
            return caption
        now = time.monotonic()
        if now >= self.deadline:
            raise RuntimeError("未能唤起字幕。请确认豆包已登录、字幕快捷键有效，然后重试。")
        if not self.sent:
            # A fresh process needs time to register its global shortcut.
            # Background startup deliberately has no visible main window. Allow
            # time for shortcut registration without waiting for window visibility.
            if self.spawned and now - self.started < 4:
                return None
            if running_doubao_exe() is None:
                raise RuntimeError("豆包已经退出，请再次点击“一键开启字幕”。")
            self.key = read_caption_shortcut()
            self.sent = send_shortcut(self.key)
        return None


def request_caption_close(window, frame=None):
    # Identify the actual close icon in this caption's WGC frame, then dispatch
    # its client mouse messages. Doubao ignores WM_CLOSE on this tool window.
    # Never close the main chat window or terminate the Doubao process.
    if window.title or window.score < 150 or window.class_name != "Chrome_WidgetWin_1":
        raise RuntimeError("目标不是豆包字幕窗口，已取消关闭。")
    if not still_same_window(window):
        return
    if frame is None:
        raise RuntimeError("字幕画面尚未准备完成，请稍后重试。")
    import cv2
    base = RESOURCE_DIR
    reference = cv2.imread(str(base / "assets" / "caption-close.png"), cv2.IMREAD_GRAYSCALE)
    if reference is None:
        raise RuntimeError("缺少字幕关闭按钮识别图，请保留完整程序文件夹。")
    user32.GetDpiForWindow.argtypes = [W.HWND]
    user32.GetDpiForWindow.restype = W.UINT
    scale = max(1, user32.GetDpiForWindow(window.hwnd)) / 96
    if abs(scale - 1) > 0.05:
        reference = cv2.resize(reference, None, fx=scale, fy=scale)
    height, width = frame.shape[:2]
    left = int(width * 0.7)
    gray = cv2.cvtColor(frame[:min(height, round(75 * scale)), left:], cv2.COLOR_BGRA2GRAY)
    if gray.shape[0] < reference.shape[0] or gray.shape[1] < reference.shape[1]:
        raise RuntimeError("字幕工具栏过小，无法识别关闭按钮。")
    _, score, _, point = cv2.minMaxLoc(cv2.matchTemplate(gray, reference, cv2.TM_CCOEFF_NORMED))
    if score < 0.85:
        raise RuntimeError("未识别到字幕条的关闭按钮，请展开字幕条后重试。")
    rect = W.RECT()
    user32.GetClientRect.argtypes = [W.HWND, C.POINTER(W.RECT)]
    if not user32.GetClientRect(window.hwnd, C.byref(rect)):
        raise RuntimeError("无法读取字幕窗口位置。")
    x = round((left + point[0] + reference.shape[1] / 2) * (rect.right - rect.left) / width)
    y = round((point[1] + reference.shape[0] / 2) * (rect.bottom - rect.top) / height)
    if not still_same_window(window):
        return
    logging.info("Caption close button matched score=%.3f at client %s,%s", score, x, y)
    user32.PostMessageW.argtypes = [W.HWND, W.UINT, W.WPARAM, W.LPARAM]
    user32.PostMessageW.restype = W.BOOL
    point_message = (y << 16) | x
    for message, flags in [(0x0200, 0), (0x0201, 1), (0x0202, 0)]:
        if not user32.PostMessageW(window.hwnd, message, flags, point_message):
            raise RuntimeError("无法调用字幕关闭按钮，请确认豆包与本程序以相同权限运行。")


class SubtitleCloser:
    def __init__(self, window, capture=None):
        self.window = window
        self.capture = capture
        self.sent = False
        self.deadline = time.monotonic() + 5

    def poll(self):
        if not still_same_window(self.window) or not user32.IsWindowVisible(self.window.hwnd):
            return True
        if time.monotonic() >= self.deadline:
            raise RuntimeError("豆包未关闭字幕，请重试或点击字幕条的关闭按钮。")
        if not self.sent and self.capture:
            _, frame = self.capture.read()
            if frame is not None:
                request_caption_close(self.window, frame)
                self.sent = True
        return False
