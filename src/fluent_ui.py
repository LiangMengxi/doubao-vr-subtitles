"""Fluent / WinUI-inspired presentation; capture and VR stay in the controller."""
import ctypes as C
import sys
import tkinter as tk
from tkinter import ttk
from tkinter import font as tkfont
import winreg
import sv_ttk
from ui_controls import ValueSlider, SegmentedControl, rounded_points
from settings_view import SettingsView

PALETTES = {
    'light': dict(background='#f3f3f3', card='#ffffff', border='#e5e5e5',
                  text='#1b1b1b', muted='#616161', accent='#0067c0',
                  track='#8a8a8a', preview='#f7f7f7', focus='#1b1b1b',
                  accent_text='#ffffff', hover='#f0f0f0'),
    'dark': dict(background='#202020', card='#2d2d2d', border='#3d3d3d',
                 text='#ffffff', muted='#b4b4b4', accent='#60cdff',
                 track='#9a9a9a', preview='#252525', focus='#ffffff',
                 accent_text='#101010', hover='#383838'),
}
APPEARANCES = {'跟随系统': 'system', '浅色': 'light', '深色': 'dark'}


def remove_button_focus_images(root, style, mode):
    """Use Fluent button images without a separate focus-border state."""
    namespace = f'::ttk::theme::sv_{mode}::I'
    for target, element, prefix, label in (
        ('TButton', 'BridgeButton.button', 'button', 'Button'),
        ('Accent.TButton', 'BridgeAccentButton.button', 'button-accent', 'AccentButton'),
    ):
        if element not in style.element_names():
            images = {state: root.tk.call('set', f'{namespace}({prefix}-{state})')
                      for state in ('rest', 'dis', 'pressed', 'hover')}
            style.element_create(element, 'image', images['rest'],
                                 ('disabled', images['dis']),
                                 ('pressed', images['pressed']),
                                 ('active', images['hover']), border=4, sticky='nsew')
        style.layout(target, [(element, {'sticky': 'nsew', 'children': [
            (f'{label}.padding', {'sticky': 'nsew', 'children': [
                (f'{label}.label', {'side': 'left', 'expand': True}),
            ]}),
        ]})])


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
        remove_button_focus_images(self.root, style, mode)
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
    """Inset well for the captured caption, drawn once and re-shaped on resize."""

    def __init__(self, parent, **kwargs):
        kwargs.setdefault('highlightthickness', 0)
        kwargs.setdefault('borderwidth', 0)
        super().__init__(parent, **kwargs)
        self.palette = None
        self.well = self.create_polygon(0, 0, 1, 1, 1, 1, smooth='raw',
                                        splinesteps=24, fill='', outline='')
        self.bind('<Configure>', lambda _: self.layout())

    def apply_palette(self, palette):
        self.palette = palette
        self.configure(bg=palette['card'])
        for item in self.find_withtag('placeholder'):
            self.itemconfigure(item, fill=palette['muted'])
        for item in self.find_withtag('hint'):
            self.itemconfigure(item, fill=palette['muted'])
        self.layout()

    def layout(self):
        if not self.palette:
            return
        width = max(4, self.winfo_width())
        height = max(4, self.winfo_height())
        self.coords(self.well, *rounded_points(1, 1, width - 1, height - 1, 8))
        self.itemconfigure(self.well, fill=self.palette['preview'],
                           outline=self.palette['border'])


class _PivotHandle:
    """Small compatibility shim so existing page-focus code can target a tab."""

    def __init__(self, bar, key):
        self.bar, self.key = bar, key

    def state(self, flags=()):
        if 'selected' in flags:
            self.bar.set_active(self.key)
        return ()

    def focus_set(self):
        self.bar.focus_set()


class PivotBar(tk.Canvas):
    """WinUI-style pivot: borderless labels with an accent underline."""

    HEIGHT = 32

    def __init__(self, parent, appearance, items, command):
        super().__init__(parent, height=self.HEIGHT, highlightthickness=0,
                         borderwidth=0, takefocus=True)
        self.appearance = appearance
        self.items = list(items)
        self.command = command
        self.palette = None
        self.active = self.items[0][0]
        self.hover = None
        self.font = tkfont.Font(family='Microsoft YaHei UI', size=10)
        self.boxes = {}
        self.buttons = {key: _PivotHandle(self, key) for key, _ in self.items}
        self.bind('<Configure>', lambda _: self.redraw())
        self.bind('<Button-1>', self.click)
        self.bind('<Motion>', self.motion)
        self.bind('<Leave>', lambda _: self.set_hover(None))
        self.bind('<Left>', lambda _: self.step(-1))
        self.bind('<Right>', lambda _: self.step(1))
        self.bind('<space>', lambda _: self.activate(self.active))
        appearance.register(self)

    def apply_palette(self, palette):
        self.palette = palette
        self.configure(bg=palette['background'])
        self.redraw()

    def set_active(self, key):
        if key != self.active:
            self.active = key
            self.redraw()

    def activate(self, key):
        if key == self.active:
            return
        self.active = key
        self.redraw()
        self.command(key)

    def key_at(self, x):
        for key, (x1, _, x2, _) in self.boxes.items():
            if x1 <= x <= x2:
                return key
        return None

    def motion(self, event):
        key = self.key_at(event.x)
        self.configure(cursor='hand2' if key else '')
        self.set_hover(key)

    def set_hover(self, key):
        if key != self.hover:
            self.hover = key
            self.redraw()

    def click(self, event):
        key = self.key_at(event.x)
        if key:
            self.focus_set()
            self.activate(key)

    def step(self, delta):
        keys = [key for key, _ in self.items]
        self.activate(keys[(keys.index(self.active) + delta) % len(keys)])
        return 'break'

    def redraw(self):
        if not self.palette:
            return
        palette = self.palette
        self.delete('all')
        self.boxes = {}
        x = 2
        for key, label in self.items:
            width = self.font.measure(label)
            active = key == self.active
            if active:
                color = palette['text']
            else:
                color = palette['text'] if self.hover == key else palette['muted']
            self.create_text(x, 11, text=label, anchor='w', fill=color, font=self.font)
            self.boxes[key] = (x - 8, 1, x + width + 8, 29)
            if active:
                self.create_polygon(*rounded_points(x, 25, x + width, 28, 1.5),
                                    smooth='raw', splinesteps=12,
                                    fill=palette['accent'], outline=palette['accent'])
            x += width + 30



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
    options = [(value, label) for label, value in APPEARANCES.items()]
    app.appearance_control = SegmentedControl(header, options, command=app.change_appearance)
    app.appearance_control.select(app.appearance.preference)
    app.appearance_control.pack(side='right', anchor='n', pady=7)
    app.appearance.register(app.appearance_control)

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
    app.preview_placeholder = app.preview.create_text(
        0, 0, text='等待豆包实时字幕\n点击上方「一键开启字幕」',
        tags='placeholder', justify='center', font=('Microsoft YaHei UI', 10))
    app.photo = None
    app.preview.bind('<Configure>', lambda _: app.request_preview(), add='+')
    app.appearance.register(app.preview)
    ttk.Label(body, textvariable=app.vr_status, style='CardMuted.TLabel').pack(anchor='w', pady=(5, 0))

    app.pages = {}
    app.pivot = PivotBar(outer, app.appearance, [
        ('quick', '大小与距离'),
        ('advanced', '位置与更多设置'),
        ('program', '程序设置'),
    ], app.show_page)
    app.pivot.pack(fill='x', pady=(10, 8))
    app.page_area = SettingsView(outer, app, app.appearance)
    app.page_area.pack(fill='both', expand=True)
    for key, _ in app.pivot.items:
        app.pages[key] = app.page_area
    # Keep the page_buttons mapping: page-focus switching focuses a tab handle.
    app.page_buttons = app.pivot.buttons
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
