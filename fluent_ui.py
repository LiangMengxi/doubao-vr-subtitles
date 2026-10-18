"""Fluent / WinUI-inspired presentation; capture and VR stay in the controller."""
import ctypes as C
import sys
import tkinter as tk
from tkinter import ttk
import winreg
import sv_ttk
from ui_controls import ValueSlider, rounded_points
from settings_view import SettingsView

PALETTES = {
    'light': dict(background='#f3f3f3', card='#ffffff', border='#e5e5e5',
                  text='#1b1b1b', muted='#616161', accent='#0067c0',
                  track='#8a8a8a', preview='#f6f6f6', focus='#1b1b1b'),
    'dark': dict(background='#202020', card='#2b2b2b', border='#353535',
                 text='#ffffff', muted='#b4b4b4', accent='#60cdff',
                 track='#9a9a9a', preview='#242424', focus='#ffffff'),
}
APPEARANCES = {'跟随系统': 'system', '浅色': 'light', '深色': 'dark'}


def system_theme():
    try:
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                            r'Software\Microsoft\Windows\CurrentVersion\Themes\Personalize') as key:
            return 'light' if winreg.QueryValueEx(key, 'AppsUseLightTheme')[0] else 'dark'
    except OSError:
        return 'light'


class Appearance:
    def __init__(self, root, preference='system'):
        self.root = root
        self.preference = preference if preference in APPEARANCES.values() else 'system'
        self.mode = None
        self.widgets = []
        self.pending_refresh = None
        self.on_changed = None
        root.bind('<<ThemeChanged>>', self.schedule_refresh, add='+')
        root.bind('<Map>', lambda event: root.after(50, self.titlebar) if event.widget is root else None, add='+')
        self.apply(self.preference)

    def register(self, widget):
        self.widgets.append(widget)
        widget.apply_palette(self.palette)

    def apply(self, preference):
        self.preference = preference
        mode = system_theme() if preference == 'system' else preference
        if self.mode == mode:
            return
        self.mode, self.palette = mode, PALETTES[mode]
        sv_ttk.set_theme(mode, root=self.root)
        p = self.palette
        style = ttk.Style(self.root)
        style.configure('.', font=('Microsoft YaHei UI', 10))
        style.configure('TFrame', background=p['background'])
        style.configure('TLabel', background=p['background'], foreground=p['text'])
        style.configure('Muted.TLabel', foreground=p['muted'], font=('Microsoft YaHei UI', 9))
        style.configure('Title.TLabel', font=('Microsoft YaHei UI', 18, 'bold'))
        style.configure('Surface.TFrame', background=p['card'])
        style.configure('Card.TLabel', background=p['card'], foreground=p['text'])
        style.configure('CardMuted.TLabel', background=p['card'], foreground=p['muted'], font=('Microsoft YaHei UI', 9))
        style.configure('CardHeading.TLabel', background=p['card'], foreground=p['text'], font=('Microsoft YaHei UI', 10, 'bold'))
        style.configure('CardValue.TLabel', background=p['card'], foreground=p['accent'], font=('Segoe UI Variable Display', 16, 'bold'))
        style.configure('TButton', padding=(10, 4))
        style.configure('Accent.TButton', padding=(10, 4))
        style.configure('Step.TButton', padding=(6, 1), font=('Segoe UI', 12))
        style.configure('Preset.Toggle.TButton', padding=(8, 2), font=('Microsoft YaHei UI', 9))
        style.configure('Pivot.Toolbutton', padding=(8, 6), font=('Microsoft YaHei UI', 10))
        style.map('Pivot.Toolbutton', foreground=[('selected', p['accent'])])
        style.configure('Exit.TButton', foreground='#c42b1c' if mode == 'light' else '#ff99a4')
        self.root.configure(bg=p['background'])
        self.schedule_refresh()

    def schedule_refresh(self, event=None):
        # ThemeChanged is delivered to every ttk descendant through the
        # toplevel bindtag. Only the root event announces a theme change;
        # handling each child repeats Python callbacks during theme setup.
        if event is not None and event.widget is not self.root:
            return
        if self.pending_refresh is None:
            self.pending_refresh = self.root.after_idle(self.finish_theme)

    def finish_theme(self):
        self.pending_refresh = None
        p = self.palette
        self.root.configure(bg=p['background'])
        # sv_ttk calls tk_setPalette on ThemeChanged. Clear its per-label
        # overrides so our card/secondary/accent styles remain authoritative.
        remaining = [self.root]
        while remaining:
            widget = remaining.pop()
            remaining.extend(widget.winfo_children())
            if isinstance(widget, ttk.Label):
                widget.configure(background='', foreground='')
        for widget in self.widgets:
            if widget.winfo_exists():
                widget.apply_palette(p)
        if self.on_changed:
            self.on_changed()
        self.titlebar()

    def titlebar(self):
        if not self.root.winfo_exists():
            return
        try:
            user = C.WinDLL('user32')
            user.GetAncestor.argtypes = [C.c_void_p, C.c_uint]
            user.GetAncestor.restype = C.c_void_p
            hwnd = user.GetAncestor(self.root.winfo_id(), 2)
            dwm = C.WinDLL('dwmapi')
            dwm.DwmSetWindowAttribute.argtypes = [C.c_void_p, C.c_uint, C.c_void_p, C.c_uint]
            dark = C.c_int(self.mode == 'dark')
            dwm.DwmSetWindowAttribute(hwnd, 20, C.byref(dark), C.sizeof(dark))
            if sys.getwindowsversion().build >= 22000:
                rounded = C.c_int(2)
                dwm.DwmSetWindowAttribute(hwnd, 33, C.byref(rounded), C.sizeof(rounded))
                r, g, b = (int(self.palette['background'][i:i+2], 16) for i in (1, 3, 5))
                caption = C.c_uint(r | g << 8 | b << 16)
                dwm.DwmSetWindowAttribute(hwnd, 35, C.byref(caption), C.sizeof(caption))
        except (OSError, AttributeError):
            pass  # Styling is optional on older Windows; native controls remain.


class Card(tk.Frame):
    def __init__(self, parent, appearance, padding=(12, 10)):
        super().__init__(parent, borderwidth=0, highlightthickness=0)
        self.canvas = tk.Canvas(self, borderwidth=0, highlightthickness=0, takefocus=False)
        self.canvas.place(x=0, y=0, relwidth=1, relheight=1)
        self.shape = self.canvas.create_polygon(*rounded_points(0,0,1,1,0), smooth='raw', splinesteps=24, width=1)
        self.content = ttk.Frame(self, style='Surface.TFrame')
        self.content.pack(fill='both', expand=True, padx=padding[0], pady=padding[1])
        self.drawn_size = None
        self.bind('<Configure>', self.redraw)
        appearance.register(self)

    def apply_palette(self, palette):
        self.configure(bg=palette['background'])
        self.canvas.configure(bg=palette['background'])
        self.canvas.itemconfigure(self.shape, fill=palette['card'], outline=palette['border'])
        self.redraw()

    def redraw(self, event=None):
        size = (event.width, event.height) if event is not None else (self.winfo_width(), self.winfo_height())
        if size == self.drawn_size:
            return
        self.drawn_size = size
        w, h = max(2, size[0]) - 1, max(2, size[1]) - 1
        self.canvas.coords(self.shape, *rounded_points(1,1,w,h,12))


class Preview(tk.Canvas):
    def apply_palette(self, palette):
        self.configure(bg=palette['preview'])
        for item in self.find_withtag('placeholder'):
            self.itemconfigure(item, fill=palette['muted'])


def cache_page_focus(app):
    """Stacked pages stay mapped; exclude covered controls from Tab traversal."""
    app.page_focus_widgets = {}
    for key, page in app.pages.items():
        controls = []
        remaining = list(page.winfo_children())
        while remaining:
            widget = remaining.pop()
            remaining.extend(widget.winfo_children())
            if isinstance(widget, (ttk.Button, ttk.Combobox, ttk.Checkbutton, ValueSlider)):
                controls.append((widget, widget.cget('takefocus')))
                widget.configure(takefocus=0)
        app.page_focus_widgets[key] = controls
    app.focus_page = None


def set_page_focus(app, key):
    """Restore native keyboard behavior only for the currently raised page."""
    previous = app.focus_page
    if previous == key:
        return
    focused = app.root.focus_get()
    previous_controls = app.page_focus_widgets.get(previous, ())
    for widget, _ in previous_controls:
        widget.configure(takefocus=0)
    for widget, takefocus in app.page_focus_widgets[key]:
        widget.configure(takefocus=takefocus)
    app.focus_page = key
    if any(focused is widget for widget, _ in previous_controls):
        app.page_buttons[key].focus_set()


def build_ui(app, limits, preference):
    root = app.root
    app.appearance = Appearance(root, preference)
    app.preset_buttons = []
    app.value_labels = {}
    outer = ttk.Frame(root, padding=(20, 14))
    outer.pack(fill='both', expand=True)
    header = ttk.Frame(outer)
    header.pack(fill='x', pady=(0, 12))
    title = ttk.Frame(header)
    title.pack(side='left')
    ttk.Label(title, text='豆包 VR 字幕桥', style='Title.TLabel').pack(anchor='w')
    ttk.Label(title, text='豆包实时字幕 · SteamVR', style='Muted.TLabel').pack(anchor='w', pady=(2, 0))
    selected = next(label for label, value in APPEARANCES.items() if value == app.appearance.preference)
    app.appearance_choice = tk.StringVar(value=selected)
    chooser = ttk.Combobox(header, textvariable=app.appearance_choice,
                           values=list(APPEARANCES), width=8, state='readonly')
    chooser.pack(side='right', anchor='n', pady=8)
    chooser.bind('<<ComboboxSelected>>', app.change_appearance)

    connection = Card(outer, app.appearance, padding=(12, 10))
    connection.pack(fill='x')
    body = connection.content
    actions = ttk.Frame(body, style='Surface.TFrame')
    actions.pack(fill='x')
    app.launch_button = ttk.Button(actions, text='一键开启字幕', style='Accent.TButton', command=app.toggle_subtitles)
    app.launch_button.pack(side='left', fill='x', expand=True, padx=(0, 8))
    app.start_button = ttk.Button(actions, text='开启 VR 悬浮', command=app.toggle_vr)
    app.start_button.pack(side='left', fill='x', expand=True)
    status_label = ttk.Label(body, textvariable=app.status, style='CardMuted.TLabel', wraplength=490)
    status_label.pack(anchor='w', pady=(5, 5))
    body.bind('<Configure>', lambda event: status_label.configure(wraplength=max(220, event.width)))
    app.preview = Preview(body, height=82, highlightthickness=0, borderwidth=0)
    app.preview.pack(fill='x')
    app.preview_image = app.preview.create_image(0, 0, state='hidden')
    app.preview_placeholder = app.preview.create_text(0, 0, text='等待豆包实时字幕', tags='placeholder', font=('Microsoft YaHei UI', 10))
    app.photo = None
    app.preview.bind('<Configure>', lambda _: app.request_preview())
    app.appearance.register(app.preview)
    ttk.Label(body, textvariable=app.vr_status, style='CardMuted.TLabel').pack(anchor='w', pady=(5, 0))

    pivot = ttk.Frame(outer)
    pivot.pack(fill='x', pady=(8, 6))
    app.pages = {}
    app.page_buttons = {}
    app.page_markers = {}
    app.page_area = SettingsView(outer, app, app.appearance)
    app.page_area.pack(fill='both', expand=True)
    for key, label in [('quick', '大小与距离'), ('advanced', '位置与更多设置'), ('program', '程序设置')]:
        group = ttk.Frame(pivot)
        group.pack(side='left', padx=(0, 16))
        button = ttk.Button(group, text=label, style='Pivot.Toolbutton', command=lambda k=key: app.show_page(k))
        button.pack()
        marker = tk.Frame(group, height=3)
        marker.pack(fill='x', padx=8)
        app.page_buttons[key], app.page_markers[key] = button, marker
        app.pages[key] = app.page_area
    app.show_page('quick')
    app.appearance.on_changed = lambda: app.show_page(app.current_page)
    app.update_labels()

    footer = ttk.Frame(outer)
    footer.pack(fill='x', pady=(4, 0))
    ttk.Button(footer, text='恢复默认', command=app.reset_settings).pack(side='left')
    ttk.Button(footer, text='最小化', command=app.minimize).pack(side='left', padx=8)
    app.exit_button = ttk.Button(footer, text='退出并关闭豆包' if app.exit_close_doubao.get() else '退出并关闭 VR 字幕', style='Exit.TButton', command=app.exit_app)
    app.exit_button.pack(side='right')
    credits = ttk.Frame(outer)
    credits.pack(fill='x', pady=(7, 0))
    ttk.Label(credits, text='关闭窗口会同时关闭 VR 字幕', style='Muted.TLabel').pack(side='left')
    ttk.Label(credits, text='创作者：凉梦曦', style='Muted.TLabel').pack(side='right')
