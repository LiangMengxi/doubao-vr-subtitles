"""Explicit full-app exit, used only by the dedicated exit action."""
import logging
from pathlib import Path
import threading
import psutil
from subtitle_launcher import installed_doubao_exe, running_doubao_exe


def stop_doubao():
    trusted = set()
    for finder in (installed_doubao_exe, running_doubao_exe):
        try:
            path = finder()
            if path:
                trusted.add(Path(path).resolve())
        except (OSError, RuntimeError, psutil.Error):
            pass
    targets, unresolved = {}, set()
    for proc in psutil.process_iter(['name', 'exe', 'ppid']):
        if (proc.info.get('name') or '').lower() != 'doubao.exe':
            continue
        executable = proc.info.get('exe')
        if not executable:
            unresolved.add(proc.pid)
            continue
        if Path(executable).resolve() in trusted:
            targets[proc.pid] = proc
    # Include owned helpers only when they also live inside the installation.
    roots = {path.parent for path in trusted}
    for proc in list(targets.values()):
        try:
            for child in proc.children(recursive=True):
                path = Path(child.exe()).resolve()
                if any(path.is_relative_to(root) for root in roots):
                    targets[child.pid] = child
        except psutil.NoSuchProcess:
            pass
        except psutil.AccessDenied:
            logging.warning('Cannot inspect some Doubao helper processes')
    def child_first_key(proc):
        try:
            return proc.ppid() in targets
        except psutil.NoSuchProcess:
            return False
    attempted = []
    failed = set(unresolved)
    # End browser/main processes first to avoid respawning renderers.
    for proc in sorted(targets.values(), key=child_first_key):
        try:
            if proc.is_running():  # psutil also detects PID reuse here.
                proc.terminate()
                attempted.append(proc)
        except psutil.NoSuchProcess:
            pass
        except psutil.AccessDenied:
            failed.add(proc.pid)
    _, alive = psutil.wait_procs(attempted, timeout=3)
    for proc in alive:
        try:
            if proc.is_running():
                proc.kill()
        except psutil.NoSuchProcess:
            pass
        except psutil.AccessDenied:
            failed.add(proc.pid)
    _, still_alive = psutil.wait_procs(alive, timeout=1)
    failed.update(proc.pid for proc in still_alive if proc.is_running())
    logging.info('Full Doubao exit: targeted=%s unresolved=%s', len(targets), sorted(failed))
    return sorted(failed)


class DoubaoExitTask:
    def __init__(self):
        self.done = threading.Event()
        self.failed = []
        self.error = None
        self.thread = threading.Thread(target=self._run, name='DoubaoExit', daemon=True)
        self.thread.start()

    def _run(self):
        try:
            self.failed = stop_doubao()
        except Exception as exc:
            self.error = str(exc)
            logging.exception('Full Doubao exit failed')
        finally:
            self.done.set()
