"""Compact, keyboard-accessible custom controls with persistent canvas drawings."""
import tkinter as tk
from tkinter import font as tkfont


def rounded_points(x1, y1, x2, y2, radius):
    """Cubic Bezier path with true circular corners for Canvas smooth='raw'."""
    r = max(0, min(radius, (x2-x1)/2, (y2-y1)/2))
    k = r * .5522847498
    points = [(x1+r,y1),
              (x1+r,y1),(x2-r,y1),(x2-r,y1),
              (x2-r+k,y1),(x2,y1+r-k),(x2,y1+r),
              (x2,y1+r),(x2,y2-r),(x2,y2-r),
              (x2,y2-r+k),(x2-r+k,y2),(x2-r,y2),
              (x2-r,y2),(x1+r,y2),(x1+r,y2),
              (x1+r-k,y2),(x1,y2-r+k),(x1,y2-r),
              (x1,y2-r),(x1,y1+r),(x1,y1+r),
              (x1,y1+r-k),(x1+r-k,y1),(x1+r,y1)]
    return [coordinate for point in points for coordinate in point]


class ValueSlider(tk.Canvas):
    def __init__(self, parent, variable, low, high, step=0.05):
        super().__init__(parent, height=28, width=180, bg="#ffffff", highlightthickness=0, takefocus=True)
        self.variable, self.low, self.high, self.step = variable, low, high, step
        self.accent = '#0067c0'
        self.drawn_width = None
        self.drawn_value = None
        self.hovered = False
        self.track = self.create_line(12, 14, 168, 14, fill="#8a8a8a", width=3, capstyle="round")
        self.fill = self.create_line(12, 14, 12, 14, fill=self.accent, width=3, capstyle="round")
        self.thumb = self.create_oval(2, 4, 22, 24, fill="#ffffff", outline="#d4d4d4", width=1)
        self.dot = self.create_oval(7, 9, 17, 19, fill=self.accent, outline='')
        self.trace = variable.trace_add("write", self.redraw)
        self.bind("<Configure>", self.redraw)
        self.bind("<Button-1>", self.choose)
        self.bind("<B1-Motion>", self.choose)
        self.bind("<Left>", lambda _: self.nudge(-step))
        self.bind("<Right>", lambda _: self.nudge(step))
        self.bind("<MouseWheel>", lambda e: self.nudge(step if e.delta > 0 else -step))
        self.bind('<Home>', lambda _: self.set_endpoint(low))
        self.bind('<End>', lambda _: self.set_endpoint(high))
        self.bind('<Enter>', lambda _: self.highlight(True))
        self.bind('<Leave>', lambda _: self.highlight(False))
        self.bind("<Destroy>", self.remove_trace)

    def apply_palette(self, palette):
        self.accent = palette['accent']
        self.configure(bg=palette['card'])
        self.itemconfigure(self.track, fill=palette['track'])
        self.itemconfigure(self.fill, fill=self.accent)
        self.itemconfigure(self.dot, fill=self.accent)
        self.itemconfigure(self.thumb, fill=palette['card'], outline=palette['border'])

    def highlight(self, active):
        if self.hovered == active:
            return
        self.hovered = active
        x = self.coords(self.thumb)[0] + 10
        r = 6 if active else 5
        self.coords(self.dot, x-r, 14-r, x+r, 14+r)

    def set_endpoint(self, value):
        self.set_value(value)
        return 'break'

    def remove_trace(self, _):
        try:
            self.variable.trace_remove("write", self.trace)
        except tk.TclError:
            pass

    def redraw(self, *_):
        width = max(24, self.winfo_width())
        value = min(self.high, max(self.low, self.variable.get()))
        if (width, value) == (self.drawn_width, self.drawn_value):
            return
        x = 12 + (width - 24) * (value - self.low) / (self.high - self.low)
        if width != self.drawn_width:
            self.coords(self.track, 12, 14, width - 12, 14)
        self.drawn_width, self.drawn_value = width, value
        self.coords(self.fill, 12, 14, x, 14)
        self.coords(self.thumb, x - 10, 4, x + 10, 24)
        radius = 6 if self.hovered else 5
        self.coords(self.dot, x-radius, 14-radius, x+radius, 14+radius)

    def set_value(self, value):
        # Pointer motion often quantizes to the same step. Avoid notifying all
        # variable traces (including settings persistence/VR) for those events.
        if value != self.variable.get():
            self.variable.set(value)

    def choose(self, event):
        if self.focus_get() is not self:
            self.focus_set()
        value = self.low + min(1, max(0, (event.x - 12) / max(1, self.winfo_width() - 24))) * (self.high - self.low)
        self.set_value(round(min(self.high, max(self.low, round(value / self.step) * self.step)), 3))

    def nudge(self, amount):
        self.set_value(round(min(self.high, max(self.low, self.variable.get() + amount)), 3))
        return "break"


class SegmentedControl(tk.Canvas):
    """Compact Fluent segmented selector drawn on one rounded surface.

    Replaces a native combobox: the current choice is visible without opening
    anything, and the whole control stays inside the custom-drawn look.
    """

    def __init__(self, parent, options, command=None, height=28):
        self.options = list(options)
        self.command = command
        self.value = self.options[0][0]
        self.font = tkfont.Font(family='Microsoft YaHei UI', size=9)
        self.palette = None
        self.hover = None
        self.widths = [self.font.measure(label) + 20 for _, label in self.options]
        self.total = sum(self.widths) + 4
        self.height = height
        super().__init__(parent, width=self.total, height=height,
                         highlightthickness=0, borderwidth=0, takefocus=True)
        self.bind('<Configure>', lambda _: self.redraw())
        self.bind('<Button-1>', self.click)
        self.bind('<Motion>', self.motion)
        self.bind('<Leave>', lambda _: self.set_hover(None))
        self.bind('<Left>', lambda _: self.step(-1))
        self.bind('<Right>', lambda _: self.step(1))

    def apply_palette(self, palette):
        self.palette = palette
        self.configure(bg=palette['background'])
        self.redraw()

    def select(self, value):
        """Show a value without notifying the command (initial state)."""
        if value in [value for value, _ in self.options] and value != self.value:
            self.value = value
            self.redraw()

    def set(self, value):
        if value == self.value:
            return
        self.value = value
        self.redraw()
        if self.command:
            self.command()

    def index_at(self, x):
        edge = 2
        for index, width in enumerate(self.widths):
            if edge <= x < edge + width:
                return index
            edge += width
        return None

    def motion(self, event):
        index = self.index_at(event.x)
        self.configure(cursor='hand2' if index is not None else '')
        self.set_hover(index)

    def set_hover(self, index):
        if index != self.hover:
            self.hover = index
            self.redraw()

    def click(self, event):
        self.focus_set()
        index = self.index_at(event.x)
        if index is not None:
            self.set(self.options[index][0])

    def step(self, delta):
        values = [value for value, _ in self.options]
        self.set(values[(values.index(self.value) + delta) % len(values)])
        return 'break'

    def redraw(self):
        if not self.palette:
            return
        palette = self.palette
        height = self.height
        self.delete('all')
        self.create_polygon(*rounded_points(1, 1, self.total - 1, height - 1, 9),
                            smooth='raw', splinesteps=24,
                            fill=palette['card'], outline=palette['border'])
        edge = 2
        for index, (value, label) in enumerate(self.options):
            width = self.widths[index]
            x1, x2 = edge + 2, edge + width - 2
            if value == self.value:
                self.create_polygon(*rounded_points(x1, 3, x2, height - 3, 7),
                                    smooth='raw', splinesteps=24,
                                    fill=palette['accent'], outline=palette['accent'])
                color = palette['accent_text']
            else:
                color = palette['text'] if self.hover == index else palette['muted']
            self.create_text((x1 + x2) / 2, height / 2, text=label, fill=color, font=self.font)
            edge += width
