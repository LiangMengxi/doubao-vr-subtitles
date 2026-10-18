"""User data survives replacing or moving the application installation."""
import json
import os
from pathlib import Path
import sys
import errno

APP_DIR = Path(sys.executable).parent if getattr(sys, 'frozen', False) else Path(__file__).resolve().parents[1]
RESOURCE_DIR = Path(getattr(sys, '_MEIPASS', APP_DIR))
DATA_DIR = Path(os.environ.get('LOCALAPPDATA', Path.home() / 'AppData' / 'Local')) / 'DoubaoVR'


def load_config():
    """Prefer the shared profile; read an old adjacent file before migration."""
    for path in (DATA_DIR / 'settings.json', DATA_DIR / 'settings.json.bak', APP_DIR / 'settings.json'):
        try:
            value = json.loads(path.read_text(encoding='utf-8'))
            if isinstance(value, dict):
                return value
        except (OSError, ValueError):
            continue
    return {}


def migrate_legacy_settings():
    """Copy the original bytes once; an existing profile always takes priority."""
    target = DATA_DIR / 'settings.json'
    if target.exists():
        return
    legacy = APP_DIR / 'settings.json'
    try:
        raw = legacy.read_text(encoding='utf-8')
        if not isinstance(json.loads(raw), dict):
            return
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        with target.open('x', encoding='utf-8') as stream:
            stream.write(raw)
    except (OSError, ValueError):
        pass


def save_config(values):
    DATA_DIR.mkdir(parents=True, exist_ok=True)
    saved = load_config()
    saved.update(values)
    target = DATA_DIR / 'settings.json'
    temporary = target.with_suffix('.json.tmp')
    temporary.write_text(json.dumps(saved, indent=2, ensure_ascii=False), encoding='utf-8')
    try:
        temporary.replace(target)
    except OSError as exc:
        if exc.errno != errno.EXDEV and getattr(exc, 'winerror', None) != 17:
            raise
        # Some encrypted/redirected Windows profiles reject replace as a
        # cross-device operation even within one directory. Keep a recoverable
        # copy before writing through the existing file instead.
        if target.exists():
            target.with_suffix('.json.bak').write_bytes(target.read_bytes())
        with target.open('w', encoding='utf-8') as stream:
            stream.write(temporary.read_text(encoding='utf-8'))
            stream.flush()
            os.fsync(stream.fileno())
        temporary.unlink()
