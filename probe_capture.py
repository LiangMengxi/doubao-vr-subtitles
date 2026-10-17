"""Bounded diagnostic: capture one Doubao window; optionally test VR for N seconds."""
import argparse
import json
import time
from pathlib import Path
from PIL import Image
from capture_source import WindowCapture, to_rgba
from window_source import doubao_windows


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--seconds", type=float, default=8)
    parser.add_argument("--vr", action="store_true")
    args = parser.parse_args()
    windows = doubao_windows()
    print(json.dumps([w.__dict__ for w in windows], ensure_ascii=False), flush=True)
    if not windows or windows[0].score < 150:
        raise RuntimeError("没有发现高置信度的豆包字幕窗口，请先打开实时字幕。")
    capture = WindowCapture(windows[0]).start()
    overlay = None
    last_seq = -1
    saved = False
    try:
        if args.vr:
            from vr_overlay import SubtitleOverlay
            overlay = SubtitleOverlay('local.doubao.subtitle.probe')
            overlay.configure()
            print("OpenVR overlay created", flush=True)
        deadline = time.monotonic() + args.seconds
        while time.monotonic() < deadline:
            seq, frame = capture.read()
            if frame is not None and seq != last_seq:
                rgba = to_rgba(frame)
                if not saved:
                    Path("artifacts").mkdir(exist_ok=True)
                    Image.fromarray(rgba).save("artifacts/caption-capture.png")
                    print(f"Captured HWND={windows[0].hwnd}: {rgba.shape[1]}x{rgba.shape[0]}, RGB min={rgba[:,:,:3].min()} max={rgba[:,:,:3].max()}, alpha={rgba[:,:,3].min()}..{rgba[:,:,3].max()}", flush=True)
                    saved = True
                if overlay:
                    if not overlay.submit(rgba):
                        time.sleep(0.03)
                        continue
                    if last_seq == -1:
                        # Validate the uploaded GPU resource, not headset output.
                        import numpy as np
                        texture = overlay.pool.textures[overlay.pool.index]
                        returned = overlay.pool.readback(texture)
                        np.testing.assert_array_equal(returned, rgba)
                        Image.fromarray(returned).save("artifacts/gpu-readback.png")
                        print("PASS: uploaded GPU texture matches every RGBA pixel; OpenVR submission accepted", flush=True)
                last_seq = seq
            if capture.error:
                raise RuntimeError(capture.error)
            time.sleep(0.03)
        if not saved:
            raise RuntimeError("在限定时间内没有收到帧。")
        print(f"PASS: frames={capture.frames}; overlay={'submitted' if overlay else 'not requested'}", flush=True)
    finally:
        if overlay:
            overlay.close()
        capture.stop()


if __name__ == "__main__":
    main()
