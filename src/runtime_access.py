"""Read-only checks for the Windows permissions required by window capture."""
import ctypes as C
from ctypes import wintypes as W


def current_integrity_level():
    """Return the token's mandatory integrity RID, or None if unavailable."""
    kernel = C.WinDLL('kernel32', use_last_error=True)
    security = C.WinDLL('advapi32', use_last_error=True)
    kernel.GetCurrentProcess.restype = W.HANDLE
    kernel.CloseHandle.argtypes = [W.HANDLE]
    security.OpenProcessToken.argtypes = [W.HANDLE, W.DWORD, C.POINTER(W.HANDLE)]
    security.GetTokenInformation.argtypes = [W.HANDLE, C.c_int, C.c_void_p, W.DWORD, C.POINTER(W.DWORD)]
    security.IsValidSid.argtypes = [C.c_void_p]
    security.GetSidSubAuthorityCount.argtypes = [C.c_void_p]
    security.GetSidSubAuthorityCount.restype = C.POINTER(C.c_ubyte)
    security.GetSidSubAuthority.argtypes = [C.c_void_p, W.DWORD]
    security.GetSidSubAuthority.restype = C.POINTER(W.DWORD)
    token = W.HANDLE()
    if not security.OpenProcessToken(kernel.GetCurrentProcess(), 0x0008, C.byref(token)):
        return None
    try:
        size = W.DWORD()
        security.GetTokenInformation(token, 25, None, 0, C.byref(size))
        if size.value < C.sizeof(C.c_void_p) or size.value > 65536:
            return None
        buffer = C.create_string_buffer(size.value)
        if not security.GetTokenInformation(token, 25, buffer, size.value, C.byref(size)):
            return None
        sid = C.c_void_p.from_buffer(buffer).value
        if not sid or not security.IsValidSid(sid):
            return None
        count = security.GetSidSubAuthorityCount(sid)[0]
        return security.GetSidSubAuthority(sid, count - 1)[0] if count else None
    finally:
        kernel.CloseHandle(token)


def startup_access_error():
    level = current_integrity_level()
    if level is not None and level < 0x2000:  # SECURITY_MANDATORY_MEDIUM_RID
        return (
            '当前程序运行在 Windows“低完整性”环境中，无法捕获字幕窗口，'
            '也可能无法控制豆包或保存设置。\n\n'
            '请将正式发行包完整解压或安装到普通应用目录，例如 '
            'D:\\APP\\DoubaoVR，随后从该目录启动。'
            '请勿安装到带隔离权限的开发工作区。\n\n'
            '现有设置会保留，无需关闭安全软件。'
        )
    return None
