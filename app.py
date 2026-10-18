"""Doubao VR bridge desktop controller."""
import os
# This app only processes image arrays, with no BLAS matrix workloads. Set the
# process-local limit before NumPy loads, preserving any explicit user setting.
os.environ.setdefault('OPENBLAS_NUM_THREADS', '1')

import json
import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path
import sys
import time
import threading
import atexit
import queue
import tkinter as tk

from PIL import Image, ImageTk

from capture_source import WindowCapture, to_rgba
from window_source import doubao_windows, still_same_window, user32
from vr_overlay import SubtitleOverlay
from subtitle_launcher import SubtitleLauncher, SubtitleCloser, caption_candidate
from fluent_ui import build_ui as build_fluent_ui, APPEARANCES, set_page_focus
from tray_controller import TrayController
from doubao_exit import DoubaoExitTask

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


def load_appearance():
    try:
        choice = json.loads((BASE / 'settings.json').read_text(encoding='utf-8')).get('appearance', 'system')
        return choice if choice in APPEARANCES.values() else 'system'
    except (OSError, ValueError, TypeError, AttributeError):
        return 'system'


def load_behavior():
    defaults = {'minimize_to_tray': False, 'exit_close_doubao': True, 'show_capture_border': False}
    try:
        saved = json.loads((BASE / 'settings.json').read_text(encoding='utf-8'))
        for key in defaults:
            if type(saved.get(key)) is bool:
                defaults[key] = saved[key]
    except (OSError, ValueError, TypeError, AttributeError):
        pass
    return defaults


class BridgeApp:
    def __init__(self, root):
        self.root = root
        self.root.title("豆包 VR 字幕桥")
        height = min(660, self.root.winfo_screenheight() - 90)
        x = max(10, self.root.winfo_screenwidth() - 590)
        self.root.geometry(f"560x{height}+{x}+30")
        self.root.minsize(520, min(650, height))
        try:
            icon_path = Path(getattr(sys, "_MEIPASS", Path(__file__).parent)) / "assets" / "bridge.png"
            self.app_icon = ImageTk.PhotoImage(Image.open(icon_path))
            self.root.iconphoto(True, self.app_icon)
        except OSError:
            logging.warning("Application icon is unavailable")
        self.capture_stoppers = []
        self.tray = None
        self.tray_pending = False
        self.tray_deadline = 0
        self.exit_task = None
        self.closing = False
        self.capture = None
        self.overlay = None
        self.source = None
        self.windows = []
        self.last_seq = -1
        self.last_rgba = None
        self.converted_token = None
        self.overlay_token = None
        self.preview_pending = True
        self.preview_signature = None
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
        behavior = load_behavior()
        self.minimize_to_tray = tk.BooleanVar(value=behavior['minimize_to_tray'])
        self.exit_close_doubao = tk.BooleanVar(value=behavior['exit_close_doubao'])
        self.show_capture_border = tk.BooleanVar(value=behavior['show_capture_border'])
        self.status = tk.StringVar(value="正在查找豆包实时字幕…")
        self.vr_status = tk.StringVar(value="VR 悬浮未开启")
        self.detail = tk.StringVar(value="只捕获选中的豆包窗口")
        self.build_ui()
        for var in self.vars.values():
            var.trace_add("write", self.settings_changed)
        self.root.protocol("WM_DELETE_WINDOW", self.close)
        self.root.after(150, self.discover)
        self.root.after(50, self.tick)
        self.root.after(100, self.refresh_preview)
        self.root.after(500, self.refresh_caption_switch)
        self.root.bind("<Destroy>", self.on_root_destroy, add="+")
        self.root.bind('<Unmap>', self.on_unmap, add='+')
        self.root.after(150, self.poll_tray)

    def build_ui(self):
        build_fluent_ui(self, LIMITS, load_appearance())
        self.root.after(2000, self.refresh_appearance)

    def show_page(self, key):
        set_page_focus(self, key)
        self.current_page = key
        p = self.appearance.palette
        self.pages[key].tkraise()
        for name, page in self.pages.items():
            self.page_buttons[name].state(["selected" if name == key else "!selected"])
            self.page_markers[name].configure(bg=p["accent"] if name == key else p["background"])

    def change_appearance(self, _=None):
        self.appearance.apply(APPEARANCES[self.appearance_choice.get()])
        self.show_page(self.current_page)
        self.save_settings()

    def refresh_appearance(self):
        if self.closing:
            return
        if self.appearance.preference == "system":
            previous = self.appearance.mode
            self.appearance.apply("system")
            if previous != self.appearance.mode:
                self.show_page(self.current_page)
        self.root.after(2000, self.refresh_appearance)

    def adjust(self, key, step):
        low, high = LIMITS[key]
        self.vars[key].set(round(min(high, max(low, self.vars[key].get() + step)), 3))

    def behavior_changed(self):
        self.exit_button.configure(text='退出并关闭豆包' if self.exit_close_doubao.get() else '退出并关闭 VR 字幕')
        self.save_settings()

    def capture_border_changed(self):
        self.save_settings()
        self.reconfigure_capture_border()

    def reconfigure_capture_border(self):
        if self.closing or not self.capture or not self.source:
            return
        desired = self.show_capture_border.get()
        if self.capture.draw_border == desired:
            return
        if self.closer:
            # The close-button matcher must finish using its current capture.
            self.root.after(100, self.reconfigure_capture_border)
            return
        old_capture = self.capture
        try:
            replacement = WindowCapture(self.source, draw_border=desired).start()
        except Exception:
            logging.exception('Cannot change capture border')
            self.show_capture_border.set(old_capture.draw_border)
            self.save_settings()
            self.status.set('无法切换捕获黄框，已保留原有捕获设置。')
            return
        self.capture = replacement
        self.last_seq = -1
        self.capture_started = time.monotonic()
        self.no_frame_warned = False
        self.dirty = True
        # Keep the preview and VR texture while the new session receives a frame.
        # Retiring the old native session must not block the Tk event loop.
        def retire():
            try:
                old_capture.stop()
            except Exception:
                logging.exception('Previous capture cleanup failed')
        stopper = threading.Thread(target=retire, name='CaptureBorderCleanup', daemon=True)
        self.capture_stoppers.append(stopper)
        stopper.start()
        logging.info('Capture border requested: %s', desired)

    def minimize(self):
        if self.closing:
            return
        if not self.minimize_to_tray.get():
            self.root.iconify()
            return
        try:
            if self.tray is None or self.tray.error or self.tray.stopped:
                if self.tray:
                    self.tray.stop()
                asset_root = Path(getattr(sys, '_MEIPASS', Path(__file__).parent))
                self.tray = TrayController(asset_root / 'assets' / 'bridge.png')
            else:
                self.tray.show()
            self.tray_pending = True
            self.tray_deadline = time.monotonic() + 5
        except Exception:
            logging.exception('Cannot minimize to tray')
            self.tray_pending = False
            self.root.deiconify()
            self.status.set('托盘图标无法创建，控制窗口已保留。')

    def on_unmap(self, event):
        if event.widget is self.root and not self.closing and self.root.state() == 'iconic' and self.minimize_to_tray.get():
            self.minimize()

    def poll_tray(self):
        if self.closing:
            return
        if self.tray:
            while True:
                try:
                    action = self.tray.actions.get_nowait()
                except queue.Empty:
                    break
                if action == 'restore':
                    self.restore_window()
                elif action == 'exit':
                    self.exit_app()
                    return
            if self.tray_pending:
                if self.tray.error or time.monotonic() >= self.tray_deadline:
                    self.tray_pending = False
                    self.root.deiconify()
                    self.status.set('托盘图标未就绪，控制窗口已保留。')
                elif self.tray.ready.is_set() and self.tray.icon.visible:
                    self.tray_pending = False
                    self.root.withdraw()
                    logging.info('Controller minimized to system tray')
        self.root.after(150, self.poll_tray)

    def refresh_caption_switch(self):
        if self.closing:
            return
        try:
            if not self.launcher and not self.closer:
                # A healthy caption connection is already authoritative; avoid
                # scanning every Windows process twice a second while connected.
                if (self.source and self.source.score >= 150 and self.capture
                        and not self.capture.closed and still_same_window(self.source)
                        and user32.IsWindowVisible(self.source.hwnd)
                        and not user32.IsIconic(self.source.hwnd)):
                    self.update_caption_button(self.source)
                else:
                    self.update_caption_button(caption_candidate(doubao_windows()))
        except Exception:
            logging.exception("Caption switch refresh failed")
        finally:
            if not self.closing:
                self.root.after(1000, self.refresh_caption_switch)

    def update_caption_button(self, caption):
        text = "一键关闭字幕" if caption else "一键开启字幕"
        if self.launch_button.cget('text') != text or self.launch_button.instate(['disabled']):
            self.launch_button.configure(state="normal", text=text)

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

    def request_preview(self):
        self.preview_pending = True

    def refresh_preview(self):
        if self.closing:
            return
        if self.preview_pending and self.preview.winfo_ismapped():
            self.draw_preview()
        self.root.after(100, self.refresh_preview)

    def draw_preview(self):
        self.preview_pending = True
        if not self.preview.winfo_ismapped():
            return
        width, height = self.preview.winfo_width(), self.preview.winfo_height()
        signature = (self.converted_token, self.last_rgba is None, width, height)
        if self.preview_signature == signature:
            self.preview_pending = False
            return
        self.preview_signature = signature
        self.preview_pending = False
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
        for key, preset, button in self.preset_buttons:
            button.state(['selected' if abs(self.vars[key].get() - preset) < .001 else '!selected'])

    def values(self):
        return {k: v.get() for k, v in self.vars.items()}

    def save_settings(self):
        self.save_after = None
        try:
            target = BASE / "settings.json"
            temporary = target.with_suffix(".json.tmp")
            saved = self.values()
            saved['appearance'] = self.appearance.preference
            saved['minimize_to_tray'] = self.minimize_to_tray.get()
            saved['exit_close_doubao'] = self.exit_close_doubao.get()
            saved['show_capture_border'] = self.show_capture_border.get()
            temporary.write_text(json.dumps(saved, indent=2), encoding="utf-8")
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
        self.converted_token = None
        self.overlay_token = None
        self.last_seq = -1
        self.draw_preview()

    def connect(self, window):
        self.disconnect()
        try:
            self.capture = WindowCapture(window, draw_border=self.show_capture_border.get()).start()
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
            self.overlay_token = None
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
        self.overlay_token = None
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
                # There is no VR latency to serve when both displays are idle.
                delay = 50 if self.overlay or self.preview.winfo_ismapped() else 200
                self.root.after(delay, self.tick)

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
            self.overlay_token = None
        seq, frame = self.capture.read()
        if frame is None:
            if time.monotonic() - self.capture_started > 8 and not self.no_frame_warned:
                self.no_frame_warned = True
                self.status.set("已连接但尚未收到画面。请展开字幕条，或刷新窗口后重试。")
            return
        if not self.overlay and not self.preview.winfo_ismapped():
            return  # Keep native latest-frame capture; process it on restore.
        if seq == self.last_seq and not self.dirty:
            return
        token = (id(self.capture), seq, self.vars['crop_top'].get())
        if token != self.converted_token:
            self.last_rgba = to_rgba(frame, crop_top=token[2])
            self.converted_token = token
            self.request_preview()
        if self.detail.get() != "字幕正在同步":
            self.detail.set("字幕正在同步")
        if self.status.get() != "豆包实时字幕已连接":
            self.status.set("豆包实时字幕已连接")
        if self.overlay:
            try:
                values = self.values()
                values.pop("crop_top")
                if self.dirty:
                    self.overlay.configure(**values)
                if self.overlay_token != token:
                    if not self.overlay.submit(self.last_rgba):
                        self.dirty = True
                        return
                    self.overlay_token = token
                if self.vr_status.get() != "已开启 · 跟随头显视线":
                    self.vr_status.set("已开启 · 跟随头显视线")
            except Exception as exc:
                logging.exception("Overlay submit failed")
                self.stop_vr(f"VR 悬浮已断开：{exc}")
        self.last_seq = seq
        self.dirty = False

    def exit_app(self):
        self.close(close_doubao=self.exit_close_doubao.get())

    def close(self, close_doubao=False):
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
            if close_doubao:
                self.root.deiconify()
                self.status.set('正在关闭豆包及其后台进程…')
                self.launch_button.configure(state='disabled')
                self.start_button.configure(state='disabled')
                self.exit_button.configure(state='disabled', text='正在退出…')
                self.exit_task = DoubaoExitTask()
                self.root.after(80, self.poll_exit)
            else:
                self.root.destroy()

    def poll_exit(self):
        if not self.exit_task.done.is_set():
            self.root.after(80, self.poll_exit)
            return
        if self.exit_task.error or self.exit_task.failed:
            from tkinter import messagebox
            messagebox.showwarning('豆包未完全退出', '部分豆包进程未能关闭，请从豆包托盘手动退出；字幕桥将退出。', parent=self.root)
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
        tray = getattr(self, 'tray', None)
        if tray:
            self.tray = None
            try:
                tray.stop()
            except Exception:
                logging.exception('Tray cleanup failed')

    def on_root_destroy(self, event):
        if event.widget is self.root:
            self.closing = True
            self.cleanup()

    def restore_window(self):
        self.tray_pending = False
        if self.tray:
            try:
                self.tray.hide()
            except Exception:
                logging.exception('Cannot hide tray icon during restore')
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
    import cv2
    # Caption-sized images do not benefit from a persistent 16-worker pool.
    # Set this before the first native conversion/template match creates it.
    cv2.setNumThreads(1)
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
