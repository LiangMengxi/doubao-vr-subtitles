"""Restore the running controller when its shortcut is launched again."""
import ctypes as C
from ctypes import wintypes as W

TITLE = "豆包 VR 字幕桥"


class SingleInstance:
    def __init__(self):
        self.kernel = C.WinDLL("kernel32", use_last_error=True)
        self.user = C.WinDLL("user32", use_last_error=True)
        self.kernel.CreateMutexW.argtypes = [C.c_void_p, W.BOOL, W.LPCWSTR]
        self.kernel.CreateMutexW.restype = W.HANDLE
        self.kernel.CreateEventW.argtypes = [C.c_void_p, W.BOOL, W.BOOL, W.LPCWSTR]
        self.kernel.CreateEventW.restype = W.HANDLE
        self.kernel.WaitForSingleObject.argtypes = [W.HANDLE, W.DWORD]
        self.kernel.SetEvent.argtypes = [W.HANDLE]
        self.kernel.CloseHandle.argtypes = [W.HANDLE]
        self.mutex = self.kernel.CreateMutexW(None, False, "Local\\DoubaoVRBridge")
        self.existing = C.get_last_error() == 183
        if not self.mutex:
            raise C.WinError(C.get_last_error())
        self.event = self.kernel.CreateEventW(None, False, False, "Local\\DoubaoVRBridge.Restore")

    def request_restore(self):
        self.user.FindWindowW.argtypes = [W.LPCWSTR, W.LPCWSTR]
        self.user.FindWindowW.restype = W.HWND
        self.user.GetWindowThreadProcessId.argtypes = [W.HWND, C.POINTER(W.DWORD)]
        self.user.ShowWindowAsync.argtypes = [W.HWND, C.c_int]
        self.user.SetForegroundWindow.argtypes = [W.HWND]
        self.user.AllowSetForegroundWindow.argtypes = [W.DWORD]
        hwnd = self.user.FindWindowW(None, TITLE)
        if hwnd:
            pid = W.DWORD()
            self.user.GetWindowThreadProcessId(hwnd, C.byref(pid))
            self.user.AllowSetForegroundWindow(pid.value)
        if self.event:
            self.kernel.SetEvent(self.event)
        if hwnd:
            # Also works with older versions that do not listen to the event.
            self.user.ShowWindowAsync(hwnd, 9)
            self.user.SetForegroundWindow(hwnd)

    def restore_requested(self):
        return self.event and self.kernel.WaitForSingleObject(self.event, 0) == 0

    def close(self):
        for handle in (self.event, self.mutex):
            if handle:
                self.kernel.CloseHandle(handle)
        self.event = self.mutex = None
