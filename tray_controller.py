"""Windows notification-area icon; callbacks are handed to Tk via a queue."""
import logging
import queue
import threading
import pystray
from PIL import Image


class TrayController:
    def __init__(self, image_path):
        self.actions = queue.SimpleQueue()
        self.ready = threading.Event()
        self.error = None
        self.stopped = False
        self.desired_visible = True
        with Image.open(image_path) as image:
            self.image = image.convert('RGBA').copy()
        self.icon = pystray.Icon('DoubaoVR', self.image, '豆包 VR 字幕桥', menu=pystray.Menu(
            pystray.MenuItem('打开控制窗口', lambda *_: self.actions.put('restore'), default=True),
            pystray.Menu.SEPARATOR,
            pystray.MenuItem('退出', lambda *_: self.actions.put('exit')),
        ))
        self.thread = threading.Thread(target=self._run, name='SubtitleTray', daemon=True)
        self.thread.start()

    def _run(self):
        def setup(icon):
            try:
                if self.stopped:
                    icon.stop()
                else:
                    icon.visible = self.desired_visible
            except Exception as exc:
                self.error = str(exc)
                logging.exception('Tray icon setup failed')
                icon.stop()
            finally:
                self.ready.set()
        try:
            self.icon.run(setup=setup)
        except Exception as exc:
            self.error = str(exc)
            logging.exception('Tray icon failed')
        finally:
            self.ready.set()

    def show(self):
        self.desired_visible = True
        if self.ready.is_set() and not self.error and not self.stopped:
            self.icon.visible = True

    def hide(self):
        self.desired_visible = False
        if self.ready.is_set() and not self.error and not self.stopped:
            self.icon.visible = False

    def stop(self):
        if not self.stopped:
            self.stopped = True
            self.icon.stop()
