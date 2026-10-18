"""WGC capture into a bounded latest-frame mailbox, never a desktop capture."""
import threading
import cv2
import numpy as np
from windows_capture import WindowsCapture
from window_source import still_same_window


class WindowCapture:
    def __init__(self, window, fps=20, draw_border=True):
        self.window = window
        self.draw_border = draw_border
        self.lock = threading.Lock()
        self.latest = None
        self.sequence = 0
        self.error = None
        self.closed = False
        self.frames = 0
        self.control = None
        if not still_same_window(window):
            raise RuntimeError("字幕窗口已关闭，请刷新窗口列表。")
        self.capture = WindowsCapture(window_hwnd=window.hwnd, cursor_capture=False,
                                      draw_border=draw_border,
                                      secondary_window=False, minimum_update_interval=round(1000 / fps))

        @self.capture.event
        def on_frame_arrived(frame, control):
            if self.closed:
                control.stop()
                return
            try:
                # Native frame memory expires with the callback, so copy it here.
                pixels = frame.frame_buffer.copy(order="C")
                with self.lock:
                    self.latest = pixels
                    self.sequence += 1
                    self.frames += 1
            except Exception as exc:
                self.error = str(exc)
                control.stop()

        @self.capture.event
        def on_closed():
            self.closed = True

    def start(self):
        self.control = self.capture.start_free_threaded()
        return self

    def read(self):
        with self.lock:
            return self.sequence, self.latest

    def stop(self):
        self.closed = True
        if self.control:
            self.control.stop()
            self.control = None


def to_rgba(bgra, crop_top=0, trim=True):
    """Trim fully transparent margins, then optional toolbar pixels; keep alpha."""
    if bgra is None or bgra.ndim != 3 or bgra.shape[2] != 4:
        raise ValueError("捕获帧不是 BGRA 图像")
    image = bgra
    if trim:
        # Only retain row/column bounds: nonzero on the full mask allocates
        # two machine-sized indices for every opaque pixel in expanded views.
        opaque = image[:, :, 3] > 16
        rows = np.flatnonzero(opaque.any(axis=1))
        if rows.size:
            columns = np.flatnonzero(opaque.any(axis=0))
            image = image[rows[0]:rows[-1]+1, columns[0]:columns[-1]+1]
    top = max(0, min(int(crop_top), image.shape[0] - 1))
    cropped = image[top:]
    if cropped.size and cropped.dtype == np.uint8:
        # The native conversion preserves alpha and creates one contiguous
        # result, avoiding an advanced-index copy plus a contiguous copy.
        return cv2.cvtColor(cropped, cv2.COLOR_BGRA2RGBA)
    return np.ascontiguousarray(cropped[:, :, [2, 1, 0, 3]])
