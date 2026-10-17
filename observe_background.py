"""Read-only, bounded launch observation; does not start or control any app."""
import json
from pathlib import Path
import time
from window_source import doubao_windows


def main():
    start = time.monotonic()
    last = None
    events = []
    main_seen = False
    caption_seen = False
    while time.monotonic() - start < 40:
        windows = doubao_windows()
        state = [(w.hwnd, w.title, w.score) for w in windows]
        main_seen |= any(w.title == "豆包" for w in windows)
        caption_seen |= any(w.score >= 150 for w in windows)
        if state != last:
            entry = {"seconds": round(time.monotonic() - start, 2), "windows": state}
            print(json.dumps(entry, ensure_ascii=False), flush=True)
            events.append(entry)
            last = state
        time.sleep(0.1)
    result = {"visible_main_seen": main_seen, "caption_seen": caption_seen, "events": events}
    Path("artifacts").mkdir(exist_ok=True)
    Path("artifacts/background-launch-observation.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False), flush=True)


if __name__ == "__main__":
    main()
