"""Fluent / WinUI-inspired presentation; capture and VR stay in the controller."""
import ctypes as C
import sys
import tkinter as tk
from tkinter import ttk
import winreg
import sv_ttk
from ui_controls import ValueSlider

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
        self.shape = self.canvas.create_polygon(0, 0, 1, 0, 1, 1, smooth=True, splinesteps=24, width=1)
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
        w, h, r = max(2, size[0]) - 1, max(2, size[1]) - 1, 8
        self.canvas.coords(self.shape, r, 1, w-r, 1, w, 1, w, r, w, h-r,
                           w, h, w-r, h, r, h, 1, h, 1, h-r, 1, r, 1, 1)


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
    app.page_area = ttk.Frame(outer)
    app.page_area.pack(fill='both', expand=True)
    app.page_area.columnconfigure(0, weight=1)
    app.page_area.rowconfigure(0, weight=1)
    for key, label in [('quick', '大小与距离'), ('advanced', '位置与更多设置'), ('program', '程序设置')]:
        group = ttk.Frame(pivot)
        group.pack(side='left', padx=(0, 16))
        button = ttk.Button(group, text=label, style='Pivot.Toolbutton', command=lambda k=key: app.show_page(k))
        button.pack()
        marker = tk.Frame(group, height=3)
        marker.pack(fill='x', padx=8)
        app.page_buttons[key], app.page_markers[key] = button, marker
        app.pages[key] = ttk.Frame(app.page_area)
        # Keep page geometry stable. Raising an opaque, full-size page avoids
        # removing/repacking the previous page and exposing its old pixels.
        app.pages[key].grid(row=0, column=0, sticky='nsew')

    quick = app.pages['quick']
    for key, title, unit, step, presets in [
        ('width', '字幕大小', '%', .05, [('小字幕', .7), ('标准', 1.0), ('大字幕', 1.4)]),
        ('distance', '前方距离', '米', .1, [('近 · 0.8 米', .8), ('适中 · 1.2 米', 1.2), ('远 · 1.8 米', 1.8)]),
    ]:
        card = Card(quick, app.appearance)
        card.pack(fill='x', pady=(0, 8))
        block = card.content
        heading = ttk.Frame(block, style='Surface.TFrame')
        heading.pack(fill='x')
        ttk.Label(heading, text=title, style='CardHeading.TLabel').pack(side='left')
        value = tk.StringVar()
        app.value_labels[key] = value, unit
        ttk.Label(heading, textvariable=value, style='CardValue.TLabel').pack(side='right')
        row = ttk.Frame(block, style='Surface.TFrame')
        row.pack(fill='x', pady=(1, 4))
        ttk.Button(row, text='−', width=2, style='Step.TButton', command=lambda k=key, s=step: app.adjust(k, -s)).pack(side='left')
        slider = ValueSlider(row, app.vars[key], *limits[key], step=step)
        slider.pack(side='left', fill='x', expand=True, padx=8)
        app.appearance.register(slider)
        ttk.Button(row, text='+', width=2, style='Step.TButton', command=lambda k=key, s=step: app.adjust(k, s)).pack(side='right')
        choices = ttk.Frame(block, style='Surface.TFrame')
        choices.pack(fill='x')
        for column, (label, preset) in enumerate(presets):
            choices.columnconfigure(column, weight=1)
            button = ttk.Button(choices, text=label, style='Preset.Toggle.TButton', command=lambda k=key, v=preset: app.vars[k].set(v))
            button.grid(row=0, column=column, sticky='ew', padx=(0 if column == 0 else 6, 0))
            app.preset_buttons.append((key, preset, button))

    advanced = app.pages['advanced']
    settings = Card(advanced, app.appearance, padding=(12, 8))
    settings.pack(fill='x', pady=(0, 8))
    panel = settings.content
    panel.columnconfigure(1, weight=1)
    for row, (key, label, unit, step) in enumerate([
        ('x', '左右位置', '米', .02), ('y', '上下位置', '米', .02),
        ('pitch', '字幕倾斜', '°', 1), ('opacity', '不透明度', '%', .05),
        ('crop_top', '顶部裁剪', '像素', 1),
    ]):
        ttk.Label(panel, text=label, style='Card.TLabel').grid(row=row, column=0, sticky='w', pady=2)
        slider = ValueSlider(panel, app.vars[key], *limits[key], step=step)
        slider.grid(row=row, column=1, sticky='ew', padx=12)
        app.appearance.register(slider)
        value = tk.StringVar()
        app.value_labels[key] = value, unit
        ttk.Label(panel, textvariable=value, width=8, anchor='e', style='Card.TLabel').grid(row=row, column=2, sticky='e')
    source = ttk.Frame(advanced)
    source.pack(fill='x', pady=(0, 6))
    app.source_box = ttk.Combobox(source, state='readonly')
    app.source_box.pack(side='left', fill='x', expand=True, padx=(0, 8))
    app.source_box.bind('<<ComboboxSelected>>', app.select_source)
    ttk.Button(source, text='刷新', command=app.discover).pack(side='right')
    ttk.Button(advanced, text='显示完整字幕条', command=lambda: app.vars['crop_top'].set(0)).pack(anchor='w')
    program = app.pages['program']
    for title, description, variable, command in [
        ('最小化到系统托盘', '最小化后收入右下角托盘，点击图标或再次运行软件可恢复。', app.minimize_to_tray, app.behavior_changed),
        ('退出按钮同时关闭豆包', '底部和托盘的“退出”会结束豆包后台进程；上方开关独立。', app.exit_close_doubao, app.behavior_changed),
        ('显示捕获黄框', '关闭后隐藏字幕窗口的捕获黄框，切换立即生效。', app.show_capture_border, app.capture_border_changed),
    ]:
        card = Card(program, app.appearance, padding=(12, 6))
        card.pack(fill='x', pady=(0, 8))
        row = ttk.Frame(card.content, style='Surface.TFrame')
        row.pack(fill='x')
        ttk.Label(row, text=title, style='CardHeading.TLabel').pack(side='left')
        ttk.Checkbutton(row, variable=variable, style='Switch.TCheckbutton', command=command).pack(side='right')
        description_label = ttk.Label(card.content, text=description, style='CardMuted.TLabel', wraplength=445)
        description_label.pack(anchor='w', pady=(3, 0))
        card.content.bind('<Configure>', lambda event, label=description_label: label.configure(wraplength=max(200, event.width)))
    ttk.Label(program, text='右上角 × 仍只退出字幕桥，并关闭 VR 悬浮。', style='Muted.TLabel').pack(anchor='w', pady=2)
    cache_page_focus(app)
    set_page_focus(app, 'quick')
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
