"""Build the current source separately from the installed application."""
import argparse
from pathlib import Path
import subprocess
import sys
import re

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--clean', action='store_true', help='Rebuild the analysis cache')
    args = parser.parse_args()
    version = (ROOT / 'VERSION').read_text(encoding='utf-8').strip()
    if not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise SystemExit('VERSION must contain three numeric components.')
    from PyInstaller.utils.win32.versioninfo import (
        VSVersionInfo, FixedFileInfo, StringFileInfo, StringTable, StringStruct,
        VarFileInfo, VarStruct)
    number = tuple(map(int, version.split('.'))) + (0,)
    info = VSVersionInfo(ffi=FixedFileInfo(filevers=number, prodvers=number,
                                         mask=0x3f, flags=0, OS=0x40004, fileType=1,
                                         subtype=0, date=(0, 0)), kids=[
        StringFileInfo([StringTable('080404B0', [StringStruct(key, value) for key, value in {
            'CompanyName': '凉梦曦', 'FileDescription': '豆包 VR 字幕桥主程序',
            'FileVersion': version, 'ProductVersion': version, 'InternalName': 'DoubaoVR',
            'OriginalFilename': 'DoubaoVR.exe', 'ProductName': '豆包 VR 字幕桥',
        }.items()])]), VarFileInfo([VarStruct('Translation', [2052, 1200])])])
    version_file = ROOT / 'artifacts' / 'build-spec' / 'DoubaoVR-version.txt'
    version_file.parent.mkdir(parents=True, exist_ok=True)
    version_file.write_text(str(info), encoding='utf-8')
    command = [sys.executable, '-m', 'PyInstaller', '--noconfirm', '--windowed',
               '--onedir', '--name', 'DoubaoVR',
               '--distpath', str(ROOT / 'artifacts' / 'build' / version),
               '--workpath', str(ROOT / 'artifacts' / 'build-cache'),
               '--specpath', str(ROOT / 'artifacts' / 'build-spec'),
               '--icon', str(ROOT / 'assets' / 'bridge.ico'),
               '--version-file', str(version_file)]
    if args.clean:
        command.append('--clean')
    for package in ('openvr', 'windows_capture', 'sv_ttk'):
        command.extend(['--collect-all', package])
    for name in ('bridge.png', 'bridge.ico', 'caption-close.png'):
        command.extend(['--add-data', f'{ROOT / "assets" / name};assets'])
    command.append(str(ROOT / 'src' / 'app.py'))
    subprocess.run(command, cwd=ROOT, check=True)
    from release_validation import validate_application
    digest = validate_application(ROOT / 'artifacts' / 'build' / version / 'DoubaoVR' / 'DoubaoVR.exe', version)
    print(f'Application verified: v{version}, SHA256 {digest}')


if __name__ == '__main__':
    main()
