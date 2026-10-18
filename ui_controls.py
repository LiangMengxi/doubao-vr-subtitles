"""Compact, keyboard-accessible slider with a persistent canvas drawing."""
import tkinter as tk


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
        self.focus_ring = self.create_rectangle(1, 1, 179, 27, outline='#1b1b1b', state='hidden')
        self.trace = variable.trace_add("write", self.redraw)
        self.bind("<Configure>", self.redraw)
        self.bind("<Button-1>", self.choose)
        self.bind("<B1-Motion>", self.choose)
        self.bind("<Left>", lambda _: self.nudge(-step))
        self.bind("<Right>", lambda _: self.nudge(step))
        self.bind("<MouseWheel>", lambda e: self.nudge(step if e.delta > 0 else -step))
        self.bind('<Home>', lambda _: self.set_endpoint(low))
        self.bind('<End>', lambda _: self.set_endpoint(high))
        self.bind('<FocusIn>', lambda _: self.itemconfigure(self.focus_ring, state='normal'))
        self.bind('<FocusOut>', lambda _: self.itemconfigure(self.focus_ring, state='hidden'))
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
        self.itemconfigure(self.focus_ring, outline=palette['focus'])

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
            self.coords(self.focus_ring, 1, 1, width-1, 27)
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
