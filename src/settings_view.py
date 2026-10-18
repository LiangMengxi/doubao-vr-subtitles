"""One buffered canvas for settings: page switches never expose partial widgets."""
import tkinter as tk
from tkinter import font as tkfont
from ui_controls import rounded_points


def format_value(key, value, unit):
    """Readout shared by every canvas row so units and signs stay consistent."""
    if key == 'opacity':
        return f'{value * 100:.0f}%'
    if unit == '°':
        # Round before formatting: -0.4 must read "0°", never "-0°".
        return f'{int(round(value))}°'
    if unit == '像素':
        return f'{int(round(value))} 像素'
    return f'{value:.2f} {unit}'


class SourceChoice:
    """Source selection model shared by the controller and the canvas popup."""
    def __init__(self, view):
        self.view, self.values, self.index, self.text = view, [], -1, ''

    def __setitem__(self, key, values):
        if key != 'values':
            raise KeyError(key)
        self.values = list(values)
        self.view.request_draw()

    def current(self, index=None):
        if index is not None:
            self.index = index
            self.text = self.values[index] if 0 <= index < len(self.values) else ''
            self.view.request_draw()
        return self.index

    def set(self, text):
        self.text, self.index = text, -1
        self.view.request_draw()


class SettingsView(tk.Canvas):
    def __init__(self, parent, app, appearance):
        super().__init__(parent, height=250, highlightthickness=0, borderwidth=0, takefocus=True)
        self.app, self.appearance = app, appearance
        self.page, self.pending, self.hits = 'quick', None, []
        self.popup_menu = None
        self.focus_index, self.dragging, self.hover = 0, None, None
        self.font = tkfont.Font(self, family='Microsoft YaHei UI', size=9)
        self.heading = tkfont.Font(self, family='Microsoft YaHei UI', size=10, weight='bold')
        self.value_font = tkfont.Font(self, family='Segoe UI', size=15, weight='bold')
        self.value_small = tkfont.Font(self, family='Microsoft YaHei UI', size=10, weight='bold')
        app.source_box = SourceChoice(self)
        self.bind('<Configure>', self.request_draw)
        self.bind('<Button-1>', self.click)
        self.bind('<B1-Motion>', self.drag)
        self.bind('<ButtonRelease-1>', lambda _: setattr(self, 'dragging', None))
        self.bind('<Motion>', self.motion)
        self.bind('<Leave>', lambda _: self.set_hover(None))
        self.bind('<KeyPress>', self.key)
        self.bind('<Destroy>', self.destroyed)
        appearance.register(self)

    def apply_palette(self, palette):
        self.palette = palette
        self.configure(bg=palette['background'])
        self.request_draw()

    def request_draw(self, *_):
        if self.pending is None:
            self.pending = self.after_idle(self.draw)

    def destroyed(self, event):
        if event.widget is self and self.pending is not None:
            self.after_cancel(self.pending)
            self.pending = None

    def show(self, page):
        if page != self.page:
            self.page, self.focus_index, self.dragging, self.hover = page, 0, None, None
            self.configure(cursor='')
        # All item changes are made in one event callback. Tk's canvas copies
        # its completed backing pixmap to the window in one operation.
        self.draw()

    def text(self, x, y, value, *, anchor='w', color=None, font=None, width=0):
        return self.create_text(x, y, text=value, anchor=anchor, fill=color or self.palette['text'],
                                font=font or self.font, width=width)

    def rounded(self, x1, y1, x2, y2, fill, outline=None, radius=12):
        return self.create_polygon(*rounded_points(x1,y1,x2,y2,radius),
                                   smooth='raw',splinesteps=24,fill=fill,outline=outline or fill)

    def hit(self, box, kind, action):
        self.hits.append((box, kind, action))

    def index_at(self, x, y):
        for index, (box, _, _) in enumerate(self.hits):
            x1, y1, x2, y2 = box
            if x1 <= x <= x2 and y1 <= y <= y2:
                return index
        return None

    def set_hover(self, index):
        if index != self.hover:
            self.hover = index
            self.request_draw()

    def motion(self, event):
        index = self.index_at(event.x, event.y)
        self.set_hover(index)
        kind = self.hits[index][1] if index is not None else None
        cursor = 'hand2' if kind == 'button' else 'sb_h_double_arrow' if kind == 'slider' else ''
        if self.cget('cursor') != cursor:
            self.configure(cursor=cursor)

    def button(self, box, label, action, selected=False):
        x1,y1,x2,y2 = box
        index = len(self.hits)
        if selected:
            fill, outline = self.palette['accent'], self.palette['accent']
            foreground = self.palette['accent_text']
        else:
            fill = self.palette['hover'] if index == self.hover else self.palette['card']
            outline = self.palette['border']
            foreground = None
        self.rounded(*box, fill, outline, radius=8)
        self.text((x1+x2)/2,(y1+y2)/2,label,anchor='center',color=foreground)
        self.hit(box,'button',action)

    def ellipsis(self, text, width):
        if self.font.measure(text) <= width:
            return text
        while text and self.font.measure(text + '…') > width:
            text = text[:-1]
        return text + '…'

    def slider(self, key, low, high, step, x1, x2, y):
        value = self.app.vars[key].get()
        x = x1 + (x2-x1)*(min(high,max(low,value))-low)/(high-low)
        self.create_line(x1,y,x2,y,fill=self.palette['track'],width=3,capstyle='round')
        self.create_line(x1,y,x,y,fill=self.palette['accent'],width=3,capstyle='round')
        self.create_oval(x-10,y-10,x+10,y+10,fill=self.palette['card'],outline=self.palette['border'])
        self.create_oval(x-5,y-5,x+5,y+5,fill=self.palette['accent'],outline='')
        self.hit((x1-10,y-13,x2+10,y+13),'slider',(key,low,high,step,x1,x2))

    def choose_slider(self, spec, x):
        key, low, high, step, x1, x2 = spec
        value = low + min(1,max(0,(x-x1)/(x2-x1)))*(high-low)
        value = round(min(high,max(low,round(value/step)*step)),3)
        if value != self.app.vars[key].get():
            self.app.vars[key].set(value)

    def click(self, event):
        self.focus_set()
        for index,(box,kind,action) in enumerate(self.hits):
            x1,y1,x2,y2 = box
            if x1 <= event.x <= x2 and y1 <= event.y <= y2:
                self.focus_index = index
                if kind == 'slider':
                    self.dragging = action
                    self.choose_slider(action,event.x)
                else:
                    action()
                self.request_draw()
                return 'break'

    def drag(self, event):
        if self.dragging:
            self.choose_slider(self.dragging,event.x)

    def key(self, event):
        if not self.hits:
            return
        if event.keysym in ('Tab','ISO_Left_Tab'):
            direction = -1 if event.keysym == 'ISO_Left_Tab' or event.state & 1 else 1
            target = self.focus_index + direction
            if 0 <= target < len(self.hits):
                self.focus_index = target
                return 'break'
            self.focus_index = 0 if direction > 0 else len(self.hits)-1
            return None  # Leave the canvas through normal Tk traversal.
        _, kind, action = self.hits[self.focus_index]
        if kind == 'slider' and event.keysym in ('Left','Right','Home','End'):
            key,low,high,step,_,_ = action
            value = self.app.vars[key].get()
            value = low if event.keysym == 'Home' else high if event.keysym == 'End' else value+(-step if event.keysym=='Left' else step)
            self.app.vars[key].set(round(min(high,max(low,value)),3))
            self.request_draw()
            return 'break'
        if kind != 'slider' and event.keysym in ('space','Return'):
            action()
            self.request_draw()
            return 'break'

    def popup_sources(self):
        if self.popup_menu is not None:
            self.popup_menu.destroy()
        menu = tk.Menu(self, tearoff=False)
        self.popup_menu = menu
        def select(index):
            self.app.source_box.current(index)
            self.app.select_source()
        for index,label in enumerate(self.app.source_box.values):
            menu.add_command(label=label,command=lambda i=index:select(i))
        if not self.app.source_box.values:
            menu.add_command(label='未找到字幕窗口',state='disabled')
        try:
            menu.tk_popup(self.winfo_rootx()+12,self.winfo_rooty()+226)
        finally:
            menu.grab_release()

    def quick(self, w):
        for top,key,title,low,high,step,presets in [
            (0,'width','字幕大小',.25,2.5,.05,[('小字幕',.7),('标准',1.),('大字幕',1.4)]),
            (128,'distance','前方距离',.4,3.,.1,[('近 · 0.8 米',.8),('适中 · 1.2 米',1.2),('远 · 1.8 米',1.8)]),
        ]:
            value=self.app.vars[key].get()
            self.rounded(1,top+1,w-1,top+118,self.palette['card'],self.palette['border'])
            self.text(14,top+26,title,font=self.heading)
            self.text(w-15,top+26,f'{value*100:.0f}%' if key=='width' else f'{value:.2f} 米',
                      anchor='e',color=self.palette['accent'],font=self.value_font)
            self.button((14,top+44,48,top+76),'−',lambda k=key,s=step:self.app.adjust(k,-s))
            self.slider(key,low,high,step,76,w-76,top+60)
            self.button((w-48,top+44,w-14,top+76),'+',lambda k=key,s=step:self.app.adjust(k,s))
            width=(w-42)/3
            for index,(label,preset) in enumerate(presets):
                x=14+index*(width+7)
                self.button((x,top+84,x+width,top+110),label,
                            lambda k=key,v=preset:self.app.vars[k].set(v),abs(value-preset)<.001)

    def advanced(self, w):
        rows=[('x','左右位置','米',-1.2,1.2,.02),('y','上下位置','米',-1.2,.8,.02),
              ('pitch','字幕倾斜','°',-45,45,1),('opacity','不透明度','%',.1,1.,.05),
              ('crop_top','顶部裁剪','像素',0,120,1)]
        self.rounded(1,1,w-1,176,self.palette['card'],self.palette['border'])
        for i,(key,label,unit,low,high,step) in enumerate(rows):
            y=30+i*32
            self.text(14,y,label)
            self.slider(key,low,high,step,104,w-96,y)
            self.text(w-14,y,format_value(key,self.app.vars[key].get(),unit),
                      anchor='e',color=self.palette['accent'],font=self.value_small)
        label=self.ellipsis(self.app.source_box.text or '请选择字幕窗口',w-150)
        self.button((1,186,w-71,220),label,self.popup_sources)
        self.button((w-63,186,w-1,220),'刷新',self.app.discover)
        self.button((1,224,141,258),'显示完整字幕条',lambda:self.app.vars['crop_top'].set(0))

    def program(self, w):
        for i,(title,description,var,callback) in enumerate([
            ('最小化到系统托盘','最小化后收入右下角托盘，可从图标恢复。',self.app.minimize_to_tray,self.app.behavior_changed),
            ('退出按钮同时关闭豆包','底部和托盘退出时生效，上方开关保持独立。',self.app.exit_close_doubao,self.app.behavior_changed),
            ('显示捕获黄框','关闭后隐藏捕获黄框，切换立即生效。',self.app.show_capture_border,self.app.capture_border_changed),
        ]):
            y=i*72
            index=len(self.hits)
            enabled=var.get()
            fill=self.palette['hover'] if index==self.hover else self.palette['card']
            self.rounded(1,y+1,w-1,y+64,fill,self.palette['border'])
            self.text(14,y+23,title,font=self.heading)
            self.text(14,y+47,description,color=self.palette['muted'])
            self.rounded(w-56,y+13,w-16,y+35,
                         self.palette['accent'] if enabled else self.palette['background'],
                         self.palette['accent'] if enabled else self.palette['track'],radius=11)
            x=w-26 if enabled else w-46
            knob=self.palette['accent_text'] if enabled else self.palette['muted']
            self.create_oval(x-7,y+17,x+7,y+31,fill=knob,outline='')
            def toggle(variable=var,command=callback):
                variable.set(not variable.get())
                command()
            self.hit((w-64,y+6,w-10,y+42),'button',toggle)
        self.text(14,230,'右上角 × 只退出字幕桥，并关闭 VR 悬浮。',color=self.palette['muted'])

    def draw(self):
        if self.pending is not None:
            self.after_cancel(self.pending)
            self.pending=None
        self.delete('all')
        self.hits=[]
        w=max(300,self.winfo_width())
        getattr(self,self.page)(w)
        self.focus_index=min(self.focus_index,len(self.hits)-1)
