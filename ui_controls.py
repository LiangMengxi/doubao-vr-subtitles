"""Compact, keyboard-accessible slider with a persistent canvas drawing."""
import tkinter as tk


class ValueSlider(tk.Canvas):
    def __init__(self, parent, variable, low, high, step=0.05):
        super().__init__(parent, height=28, width=180, bg="#111827", highlightthickness=0, takefocus=True)
        self.variable, self.low, self.high, self.step = variable, low, high, step
        self.track = self.create_line(10, 14, 170, 14, fill="#30415a", width=4, capstyle="round")
        self.fill = self.create_line(10, 14, 10, 14, fill="#36c4b2", width=4, capstyle="round")
        self.thumb = self.create_oval(3, 7, 17, 21, fill="#d8fff7", outline="#36c4b2", width=2)
        self.trace = variable.trace_add("write", self.redraw)
        self.bind("<Configure>", self.redraw)
        self.bind("<Button-1>", self.choose)
        self.bind("<B1-Motion>", self.choose)
        self.bind("<Left>", lambda _: self.nudge(-step))
        self.bind("<Right>", lambda _: self.nudge(step))
        self.bind("<MouseWheel>", lambda e: self.nudge(step if e.delta > 0 else -step))
        self.bind("<Destroy>", self.remove_trace)

    def remove_trace(self, _):
        try:
            self.variable.trace_remove("write", self.trace)
        except tk.TclError:
            pass

    def redraw(self, *_):
        width = max(24, self.winfo_width())
        value = min(self.high, max(self.low, self.variable.get()))
        x = 10 + (width - 20) * (value - self.low) / (self.high - self.low)
        self.coords(self.track, 10, 14, width - 10, 14)
        self.coords(self.fill, 10, 14, x, 14)
        self.coords(self.thumb, x - 7, 7, x + 7, 21)

    def choose(self, event):
        self.focus_set()
        value = self.low + min(1, max(0, (event.x - 10) / max(1, self.winfo_width() - 20))) * (self.high - self.low)
        self.variable.set(round(min(self.high, max(self.low, round(value / self.step) * self.step)), 3))

    def nudge(self, amount):
        self.variable.set(round(min(self.high, max(self.low, self.variable.get() + amount)), 3))
        return "break"
