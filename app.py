"""Doubao VR bridge desktop controller."""
import json
import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path
import sys
import time
import threading
import atexit
import tkinter as tk
from tkinter import ttk

from PIL import Image, ImageTk

from capture_source import WindowCapture, to_rgba
from window_source import doubao_windows, still_same_window, user32
from vr_overlay import SubtitleOverlay
from subtitle_launcher import SubtitleLauncher, SubtitleCloser, caption_candidate
from ui_controls import ValueSlider

BASE = Path(sys.executable).parent if getattr(sys, "frozen", False) else Path(__file__).parent
DEFAULTS = {"width": 1.0, "distance": 1.2, "x": 0.0, "y": -0.32, "opacity": 1.0, "pitch": 0.0, "crop_top": 0}
LIMITS = {"width": (0.25, 2.5), "distance": (0.4, 3.0), "x": (-1.2, 1.2), "y": (-1.2, 0.8), "opacity": (0.1, 1.0), "pitch": (-45, 45), "crop_top": (0, 120)}


def load_settings():
    values = dict(DEFAULTS)
    try:
        saved = json.loads((BASE / "settings.json").read_text(encoding="utf-8"))
        for key, default in DEFAULTS.items():
            value = float(saved.get(key, default))
            low, high = LIMITS[key]
            if low <= value <= high:
                values[key] = value
    except (OSError, ValueError, TypeError, AttributeError):
        pass
    return values


class BridgeApp:
    def __init__(self, root):
        self.root = root
        self.root.title("豆包 VR 字幕桥")
        height = min(660, self.root.winfo_screenheight() - 90)
        x = max(10, self.root.winfo_screenwidth() - 590)
        self.root.geometry(f"560x{height}+{x}+30")
        self.root.minsize(520, min(650, height))
        self.root.configure(bg="#111827")
        try:
            icon_path = Path(getattr(sys, "_MEIPASS", Path(__file__).parent)) / "assets" / "bridge.png"
            self.app_icon = ImageTk.PhotoImage(Image.open(icon_path))
            self.root.iconphoto(True, self.app_icon)
        except OSError:
            logging.warning("Application icon is unavailable")
        self.capture_stoppers = []
        self.closing = False
        self.capture = None
        self.overlay = None
        self.source = None
        self.windows = []
        self.last_seq = -1
        self.last_rgba = None
        self.last_discovery = 0
        self.capture_started = 0
        self.dirty = True
        self.overlay_hidden = False
        self.save_after = None
        self.no_frame_warned = False
        self.launcher = None
        self.closer = None
        self.launch_error = None
        self.vars = {k: tk.DoubleVar(value=v) for k, v in load_settings().items()}
        self.status = tk.StringVar(value="正在查找豆包实时字幕…")
        self.vr_status = tk.StringVar(value="VR 悬浮未开启")
        self.detail = tk.StringVar(value="只捕获选中的豆包窗口")
        self.build_ui()
        for var in self.vars.values():
            var.trace_add("write", self.settings_changed)
        self.root.protocol("WM_DELETE_WINDOW", self.close)
        self.root.after(150, self.discover)
        self.root.after(50, self.tick)
        self.root.after(500, self.refresh_caption_switch)
        self.root.bind("<Destroy>", self.on_root_destroy, add="+")

    def build_ui(self):
        style = ttk.Style()
        style.theme_use("clam")
        style.configure("TFrame", background="#111827")
        style.configure("TLabel", background="#111827", foreground="#e4edf8", font=("Microsoft YaHei UI", 10))
        style.configure("Muted.TLabel", foreground="#92a6bf", font=("Microsoft YaHei UI", 9))
        style.configure("Title.TLabel", font=("Microsoft YaHei UI", 18, "bold"))
        style.configure("Value.TLabel", foreground="#72e6cf", font=("Microsoft YaHei UI", 15, "bold"))
        style.configure("Section.TLabel", font=("Microsoft YaHei UI", 11, "bold"))
        style.configure("TButton", font=("Microsoft YaHei UI", 10), padding=(10, 6), background="#24344a", foreground="#e4edf8", borderwidth=0)
        style.map("TButton", background=[("active", "#344b66"), ("disabled", "#1b283a")], foreground=[("disabled", "#687c95")])
        style.configure("Primary.TButton", background="#137e76", foreground="#ffffff")
        style.map("Primary.TButton", background=[("active", "#199b8f"), ("disabled", "#23504e")])
        style.configure("Exit.TButton", background="#442b39", foreground="#ffdce5")
        style.map("Exit.TButton", background=[("active", "#634050")])
        style.configure("Step.TButton", padding=(10, 3), font=("Segoe UI", 13))
        style.configure("TCombobox", padding=5)
        style.configure("TNotebook", background="#111827", borderwidth=0, bordercolor="#2b3b52", lightcolor="#2b3b52", darkcolor="#2b3b52")
        style.configure("TNotebook.Tab", background="#1a293c", foreground="#a9bdd4", padding=(18, 7), bordercolor="#2b3b52", lightcolor="#2b3b52", darkcolor="#2b3b52")
        style.map("TNotebook.Tab", background=[("selected", "#244a4b")], foreground=[("selected", "#9cf2df")])

        outer = ttk.Frame(self.root, padding=(20, 16))
        outer.pack(fill="both", expand=True)
        ttk.Label(outer, text="豆包 VR 字幕桥", style="Title.TLabel").pack(anchor="w")
        ttk.Label(outer, text="豆包实时字幕 · SteamVR 悬浮显示", style="Muted.TLabel").pack(anchor="w", pady=(2, 10))
        action = ttk.Frame(outer)
        action.pack(fill="x")
        self.launch_button = ttk.Button(action, text="一键开启字幕", style="Primary.TButton", command=self.toggle_subtitles)
        self.launch_button.pack(side="left", fill="x", expand=True, padx=(0, 8))
        self.start_button = ttk.Button(action, text="开启 VR 悬浮", command=self.toggle_vr)
        self.start_button.pack(side="left", fill="x", expand=True)
        ttk.Label(outer, textvariable=self.status, wraplength=490, style="Muted.TLabel").pack(anchor="w", pady=(5, 5))

        self.preview = tk.Canvas(outer, height=88, bg="#0b1220", highlightthickness=1, highlightbackground="#2b3b52")
        self.preview.pack(fill="x")
        self.preview_image = self.preview.create_image(0, 0, state="hidden")
        self.preview_placeholder = self.preview.create_text(0, 0, text="等待豆包字幕", fill="#92a6bf", font=("Microsoft YaHei UI", 11))
        self.photo = None
        self.preview.bind("<Configure>", lambda _: self.draw_preview())
        ttk.Label(outer, textvariable=self.vr_status, style="Muted.TLabel").pack(anchor="w", pady=(5, 8))

        notebook = ttk.Notebook(outer)
        notebook.pack(fill="both", expand=True)
        quick = ttk.Frame(notebook, padding=(4, 6))
        advanced = ttk.Frame(notebook, padding=(4, 6))
        notebook.add(quick, text="大小与距离")
        notebook.add(advanced, text="位置与更多设置")
        self.value_labels = {}
        for key, title, unit, step, presets in [
            ("width", "字幕大小", "%", 0.05, [("小字幕", 0.7), ("标准", 1.0), ("大字幕", 1.4)]),
            ("distance", "前方距离", "米", 0.1, [("近 · 0.8 米", 0.8), ("适中 · 1.2 米", 1.2), ("远 · 1.8 米", 1.8)]),
        ]:
            block = ttk.Frame(quick)
            block.pack(fill="x", pady=(2, 12))
            heading = ttk.Frame(block)
            heading.pack(fill="x")
            ttk.Label(heading, text=title, style="Section.TLabel").pack(side="left")
            value = tk.StringVar()
            self.value_labels[key] = (value, unit)
            ttk.Label(heading, textvariable=value, style="Value.TLabel").pack(side="right")
            row = ttk.Frame(block)
            row.pack(fill="x", pady=(3, 5))
            ttk.Button(row, text="−", width=2, style="Step.TButton", command=lambda k=key, s=step: self.adjust(k, -s)).pack(side="left")
            ValueSlider(row, self.vars[key], *LIMITS[key], step=step).pack(side="left", fill="x", expand=True, padx=10)
            ttk.Button(row, text="+", width=2, style="Step.TButton", command=lambda k=key, s=step: self.adjust(k, s)).pack(side="right")
            choices = ttk.Frame(block)
            choices.pack(fill="x")
            for column, (label, preset) in enumerate(presets):
                choices.columnconfigure(column, weight=1)
                ttk.Button(choices, text=label, command=lambda k=key, v=preset: self.vars[k].set(v)).grid(row=0, column=column, sticky="ew", padx=(0 if column == 0 else 6, 0))

        advanced.columnconfigure(1, weight=1)
        for row, (key, label, unit, step) in enumerate([
            ("x", "左右位置", "米", 0.02), ("y", "上下位置", "米", 0.02),
            ("pitch", "字幕倾斜", "°", 1), ("opacity", "不透明度", "%", 0.05),
            ("crop_top", "顶部裁剪", "像素", 1),
        ]):
            ttk.Label(advanced, text=label, width=9).grid(row=row, column=0, sticky="w", pady=3)
            ValueSlider(advanced, self.vars[key], *LIMITS[key], step=step).grid(row=row, column=1, sticky="ew", padx=8)
            value = tk.StringVar()
            self.value_labels[key] = (value, unit)
            ttk.Label(advanced, textvariable=value, width=9, anchor="e").grid(row=row, column=2, sticky="e")
        source_row = ttk.Frame(advanced)
        source_row.grid(row=5, column=0, columnspan=3, sticky="ew", pady=(10, 4))
        self.source_box = ttk.Combobox(source_row, state="readonly")
        self.source_box.pack(side="left", fill="x", expand=True, padx=(0, 8))
        self.source_box.bind("<<ComboboxSelected>>", self.select_source)
        ttk.Button(source_row, text="刷新", command=self.discover).pack(side="right")
        ttk.Button(advanced, text="显示完整字幕条", command=lambda: self.vars["crop_top"].set(0)).grid(row=6, column=0, columnspan=3, sticky="w", pady=6)
        self.update_labels()
        footer = ttk.Frame(outer)
        footer.pack(fill="x", pady=(8, 0))
        ttk.Button(footer, text="恢复默认", command=self.reset_settings).pack(side="left")
        ttk.Button(footer, text="最小化", command=self.root.iconify).pack(side="left", padx=8)
        ttk.Button(footer, text="退出并关闭 VR 字幕", style="Exit.TButton", command=self.close).pack(side="right")
        ttk.Label(outer, text="最小化可继续显示；关闭窗口会退出 VR 字幕。", style="Muted.TLabel").pack(anchor="w", pady=(7, 0))

    def adjust(self, key, step):
        low, high = LIMITS[key]
        self.vars[key].set(round(min(high, max(low, self.vars[key].get() + step)), 3))

    def refresh_caption_switch(self):
        if self.closing:
            return
        try:
            if not self.launcher and not self.closer:
                self.update_caption_button(caption_candidate(doubao_windows()))
        except Exception:
            logging.exception("Caption switch refresh failed")
        finally:
            if not self.closing:
                self.root.after(500, self.refresh_caption_switch)

    def update_caption_button(self, caption):
        self.launch_button.configure(state="normal", text="一键关闭字幕" if caption else "一键开启字幕")

    def toggle_subtitles(self):
        if self.launcher or self.closer:
            return
        caption = caption_candidate(doubao_windows())
        if not caption:
            self.launch_subtitles()
            return
        try:
            self.launch_error = None
            if not self.source or self.source.hwnd != caption.hwnd or not self.capture:
                self.connect(caption)
            if not self.capture:
                raise RuntimeError("字幕捕获尚未就绪，请稍后重试。")
            self.closer = SubtitleCloser(caption, self.capture)
            self.launch_button.configure(state="disabled", text="正在关闭…")
            if self.overlay:
                self.overlay.hide()
                self.overlay_hidden = True
                self.vr_status.set("已连接 · 等待字幕开启")
            self.status.set("正在关闭豆包实时字幕…")
            self.poll_subtitle_close()
        except Exception as exc:
            self.finish_subtitle_close(error=exc)

    def poll_subtitle_close(self):
        if self.closing or self.closer is None:
            return
        try:
            if self.closer.poll():
                self.finish_subtitle_close()
                return
            self.root.after(100, self.poll_subtitle_close)
        except Exception as exc:
            self.finish_subtitle_close(error=exc)

    def finish_subtitle_close(self, error=None):
        self.closer = None
        self.update_caption_button(caption_candidate(doubao_windows()))
        if error:
            self.launch_error = str(error)
            self.status.set(self.launch_error)
            logging.error("Caption close failed: %s", error)
            self.discover()
        else:
            self.launch_error = None
            self.disconnect()
            self.discover()
            self.status.set("豆包实时字幕已关闭，再次点击开关即可开启。")

    def launch_subtitles(self):
        if self.launcher or self.closer:
            return
        try:
            self.launch_error = None
            self.launcher = SubtitleLauncher()
            self.launch_button.configure(state="disabled", text="正在开启…")
            self.status.set("正在唤起豆包实时字幕…")
            self.poll_subtitle_launch()
        except Exception as exc:
            self.finish_subtitle_launch(error=exc)

    def poll_subtitle_launch(self):
        if self.closing or self.launcher is None:
            return
        try:
            window = self.launcher.poll()
            if window:
                self.finish_subtitle_launch(window=window)
                return
            self.root.after(200, self.poll_subtitle_launch)
        except Exception as exc:
            self.finish_subtitle_launch(error=exc)

    def finish_subtitle_launch(self, window=None, error=None):
        self.launcher = None
        self.update_caption_button(caption_candidate(doubao_windows()))
        if error:
            logging.error("Caption launch failed: %s", error)
            self.launch_error = str(error)
            self.status.set(self.launch_error)
            return
        self.launch_error = None
        if not self.source or self.source.hwnd != window.hwnd or not self.capture:
            self.connect(window)
        self.discover()
        self.status.set("豆包实时字幕已开启并连接。")

    def draw_preview(self):
        width, height = self.preview.winfo_width(), self.preview.winfo_height()
        self.preview.coords(self.preview_image, width // 2, height // 2)
        self.preview.coords(self.preview_placeholder, width // 2, height // 2)
        if self.last_rgba is None:
            self.preview.itemconfigure(self.preview_image, state="hidden")
            self.preview.itemconfigure(self.preview_placeholder, state="normal")
            return
        image = Image.fromarray(self.last_rgba)
        image.thumbnail((max(1, width - 16), max(1, height - 12)), Image.Resampling.LANCZOS)
        if self.photo and (self.photo.width(), self.photo.height()) == image.size:
            self.photo.paste(image)
        else:
            replacement = ImageTk.PhotoImage(image)
            self.preview.itemconfigure(self.preview_image, image=replacement)
            self.photo = replacement
        self.preview.itemconfigure(self.preview_image, state="normal")
        self.preview.itemconfigure(self.preview_placeholder, state="hidden")

    def settings_changed(self, *_):
        self.update_labels()
        self.dirty = True
        if self.save_after:
            self.root.after_cancel(self.save_after)
        self.save_after = self.root.after(500, self.save_settings)

    def update_labels(self):
        for key, (var, unit) in self.value_labels.items():
            value = self.vars[key].get()
            if key in ("width", "opacity"):
                var.set(f"{value * 100:.0f}%")
            else:
                var.set(f"{value:.0f} {unit}" if key in ("crop_top", "pitch") else f"{value:.2f} {unit}")

    def values(self):
        return {k: v.get() for k, v in self.vars.items()}

    def save_settings(self):
        self.save_after = None
        try:
            target = BASE / "settings.json"
            temporary = target.with_suffix(".json.tmp")
            temporary.write_text(json.dumps(self.values(), indent=2), encoding="utf-8")
            temporary.replace(target)
        except OSError:
            logging.exception("Could not save settings")

    def reset_settings(self):
        for key, value in DEFAULTS.items():
            self.vars[key].set(value)

    def discover(self):
        if self.closer:
            return
        self.last_discovery = time.monotonic()
        self.windows = doubao_windows()
        if not self.launcher:
            self.update_caption_button(caption_candidate(self.windows))
        self.source_box["values"] = [w.label for w in self.windows]
        current = next((i for i, w in enumerate(self.windows) if self.source and w.hwnd == self.source.hwnd), None)
        if current is not None:
            self.source_box.current(current)
            return
        if self.windows and self.windows[0].score >= 150:
            self.source_box.current(0)
            self.connect(self.windows[0])
        else:
            self.source_box.set("未找到字幕窗口")
            if not self.launcher:
                self.status.set(self.launch_error or "点击“一键开启字幕”，即可唤起豆包实时字幕。")

    def select_source(self, _=None):
        index = self.source_box.current()
        if 0 <= index < len(self.windows):
            self.connect(self.windows[index])

    def disconnect(self):
        if self.overlay:
            try:
                self.overlay.hide()
                self.overlay_hidden = True
                self.vr_status.set("已连接 · 等待字幕开启")
            except Exception:
                self.stop_vr("SteamVR 已断开，请重新开启悬浮。")
        if self.capture:
            try:
                self.capture.stop()
            except Exception:
                logging.exception("Capture stop failed")
        self.capture = None
        self.source = None
        self.last_rgba = None
        self.last_seq = -1
        self.draw_preview()

    def connect(self, window):
        self.disconnect()
        try:
            self.capture = WindowCapture(window).start()
            self.source = window
            self.capture_started = time.monotonic()
            self.no_frame_warned = False
            self.dirty = True
            self.status.set("豆包实时字幕已连接")
            logging.info("Connected HWND=%s PID=%s", window.hwnd, window.pid)
        except Exception as exc:
            logging.exception("Capture startup failed")
            self.status.set(f"捕获失败：{exc}")

    def toggle_vr(self):
        if self.overlay:
            self.stop_vr()
            return
        if self.last_rgba is None:
            self.vr_status.set("请等待桌面预览出现后再开启。")
            return
        try:
            self.overlay = SubtitleOverlay()
            self.dirty = True
            self.start_button.configure(text="关闭 VR 悬浮")
            self.vr_status.set("已开启 · 跟随头显视线")
            logging.info("OpenVR overlay started")
        except Exception as exc:
            self.overlay = None
            logging.exception("OpenVR startup failed")
            self.vr_status.set(f"无法连接 SteamVR：{exc}。请确认 SteamVR 与头显已就绪。")

    def stop_vr(self, message="VR 悬浮已关闭"):
        if self.overlay:
            try:
                self.overlay.close()
            except Exception:
                logging.exception("Overlay cleanup failed")
        self.overlay = None
        try:
            self.start_button.configure(text="开启 VR 悬浮")
            self.vr_status.set(message)
        except tk.TclError:
            pass

    def tick(self):
        if self.closing:
            return
        try:
            self.update_frame()
        except Exception as exc:
            logging.exception("Frame processing failed")
            self.status.set(f"处理画面失败：{exc}")
            self.disconnect()
        finally:
            if not self.closing:
                self.root.after(50, self.tick)

    def update_frame(self):
        if self.overlay:
            try:
                if self.overlay.quit_requested():
                    self.stop_vr("SteamVR 正在退出，悬浮层已关闭。")
            except Exception as exc:
                logging.exception("SteamVR event polling failed")
                self.stop_vr(f"SteamVR 已断开：{exc}")
        if self.closer:
            return
        if not self.capture:
            if time.monotonic() - self.last_discovery > 3:
                self.discover()
            return
        if self.capture.error or self.capture.closed or not still_same_window(self.source):
            reason = self.capture.error or "字幕窗口已关闭"
            self.disconnect()
            self.status.set(f"{reason}，等待字幕窗口重新打开…")
            return
        if user32.IsIconic(self.source.hwnd):
            if self.overlay and not self.overlay_hidden:
                self.overlay.hide()
                self.overlay_hidden = True
            self.status.set("字幕窗口已最小化，请在豆包中恢复字幕条。")
            return
        if not user32.IsWindowVisible(self.source.hwnd):
            self.disconnect()
            self.status.set("字幕条已收起，等待重新展开…")
            return
        if self.overlay_hidden:
            self.overlay_hidden = False
            self.dirty = True
        seq, frame = self.capture.read()
        if frame is None:
            if time.monotonic() - self.capture_started > 8 and not self.no_frame_warned:
                self.no_frame_warned = True
                self.status.set("已连接但尚未收到画面。请展开字幕条，或刷新窗口后重试。")
            return
        if seq == self.last_seq and not self.dirty:
            return
        self.last_rgba = to_rgba(frame, crop_top=self.vars["crop_top"].get())
        self.draw_preview()
        self.detail.set("字幕正在同步")
        self.status.set("豆包实时字幕已连接")
        if self.overlay:
            try:
                values = self.values()
                values.pop("crop_top")
                if self.dirty:
                    self.overlay.configure(**values)
                if not self.overlay.submit(self.last_rgba):
                    self.dirty = True
                    return
                self.vr_status.set("已开启 · 跟随头显视线")
            except Exception as exc:
                logging.exception("Overlay submit failed")
                self.stop_vr(f"VR 悬浮已断开：{exc}")
        self.last_seq = seq
        self.dirty = False

    def close(self):
        if self.closing:
            return
        self.closing = True
        logging.info("Controller close requested")
        try:
            self.save_settings()
        except Exception:
            logging.exception("Settings save failed during exit")
        finally:
            self.cleanup()
            self.root.destroy()

    def cleanup(self):
        # Remove VR output before waiting for a capture thread. A native WGC stop
        # must never leave an invisible controller still displaying subtitles.
        overlay, self.overlay = self.overlay, None
        if overlay:
            try:
                overlay.close()
            except Exception:
                logging.exception("Final overlay cleanup failed")
        capture, self.capture = self.capture, None
        if capture:
            def stop_capture():
                try:
                    capture.stop()
                except Exception:
                    logging.exception("Final capture cleanup failed")
            stopper = threading.Thread(target=stop_capture, name="CaptureCleanup", daemon=True)
            stopper.start()
            self.capture_stoppers.append(stopper)

    def on_root_destroy(self, event):
        if event.widget is self.root:
            self.closing = True
            self.cleanup()

    def restore_window(self):
        self.root.deiconify()
        width, height = self.root.winfo_width(), self.root.winfo_height()
        sw, sh = self.root.winfo_screenwidth(), self.root.winfo_screenheight()
        x, y = self.root.winfo_x(), self.root.winfo_y()
        if x + width < 80 or x > sw - 80 or y + height < 80 or y > sh - 80:
            self.root.geometry(f"+{max(10, sw - width - 30)}+30")
        self.root.lift()
        self.root.focus_force()


def main():
    import ctypes
    ctypes.WinDLL("shell32").SetCurrentProcessExplicitAppUserModelID("DoubaoVR.SubtitleBridge")
    from single_instance import SingleInstance
    instance = SingleInstance()
    if instance.existing:
        instance.request_restore()
        instance.close()
        return
    app = None
    try:
        (BASE / "logs").mkdir(exist_ok=True)
        handler = RotatingFileHandler(BASE / "logs" / "bridge.log", maxBytes=1_000_000, backupCount=2, encoding="utf-8")
        logging.basicConfig(level=logging.INFO, handlers=[handler], format="%(asctime)s %(levelname)s %(message)s")
        root = tk.Tk()
        app = BridgeApp(root)
        atexit.register(app.cleanup)
        def check_restore():
            if not app.closing:
                if instance.restore_requested():
                    app.restore_window()
                root.after(200, check_restore)
        root.after(200, check_restore)
        root.mainloop()
    finally:
        if app:
            app.cleanup()
            atexit.unregister(app.cleanup)
            for stopper in app.capture_stoppers:
                stopper.join(timeout=1)
        instance.close()
        logging.info("Controller stopped; VR resources released")


if __name__ == "__main__":
    main()
