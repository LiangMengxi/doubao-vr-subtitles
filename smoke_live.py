"""Optional real WGC integration test with a temporary animated test window."""
import ctypes
import os
import time
import tkinter as tk
from window_source import Window, user32
from capture_source import WindowCapture, to_rgba


def main():
    root = tk.Tk()
    root.title("字幕桥 · 连续捕获测试（自动关闭）")
    root.geometry("520x160+900+150")
    label = tk.Label(root, text="准备连续捕获测试", font=("Microsoft YaHei UI", 20), bg="#25343d", fg="white")
    label.pack(expand=True, fill="both")
    root.update()
    user32.GetAncestor.argtypes = [ctypes.c_void_p, ctypes.c_uint]
    user32.GetAncestor.restype = ctypes.c_void_p
    hwnd = user32.GetAncestor(root.winfo_id(), 2)
    source = Window(hwnd, os.getpid(), root.title(), "TkTopLevel", 520, 160, 0)
    capture = WindowCapture(source).start()
    versions = []
    hashes = set()
    shapes = set()
    end = time.monotonic() + 3
    count = 0

    def step():
        nonlocal count
        count += 1
        if count == 12:
            root.geometry("520x420+900+150")
        label.configure(text=f"连续字幕更新 · {count}", bg="#25343d" if count % 2 else "#174a55")
        seq, frame = capture.read()
        if frame is not None:
            versions.append(seq)
            hashes.add(hash(to_rgba(frame).tobytes()))
            shapes.add(frame.shape)
        if time.monotonic() < end:
            root.after(120, step)
        else:
            capture.stop()
            root.destroy()

    root.after(120, step)
    root.mainloop()
    assert len(hashes) >= 5, f"Only {len(hashes)} distinct captured frames"
    assert max(versions) >= 5
    assert len(shapes) >= 2, f"Capture failed to follow resize: {shapes}"
    print(f"PASS: {max(versions)} WGC frames, {len(hashes)} distinct changing images, {len(shapes)} sizes; clean shutdown")


if __name__ == "__main__":
    main()
