"""Reject installer executables or stale builds in the application payload."""
import hashlib
from pathlib import Path


def sha256_file(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def validate_application(path, version):
    from PyInstaller.archive.readers import CArchiveReader
    from PyInstaller.utils.win32.versioninfo import read_version_info_from_executable

    path = Path(path)
    try:
        archive = CArchiveReader(str(path))
        if archive.toc.get('app', (None,))[-1] != 's':
            raise ValueError('Missing application entry point')
        modules = archive.open_embedded_archive('PYZ.pyz').toc
        if not {'window_source', 'subtitle_launcher', 'vr_overlay'} <= modules.keys():
            raise ValueError('Missing subtitle bridge modules')
        info = read_version_info_from_executable(str(path))
        strings = {}
        for child in info.kids:
            if child.__class__.__name__ == 'StringFileInfo':
                for table in child.kids:
                    strings.update((item.name, item.val) for item in table.kids)
        expected = {'OriginalFilename': 'DoubaoVR.exe', 'ProductVersion': version,
                    'ProductName': '豆包 VR 字幕桥', 'FileDescription': '豆包 VR 字幕桥主程序'}
        if any(strings.get(key) != value for key, value in expected.items()):
            raise ValueError(f'Unexpected application identity/version: {strings}')
    except Exception as exc:
        raise ValueError(f'Invalid subtitle application payload: {path}: {exc}') from exc
    return sha256_file(path)
