"""Package a built app using a runtime allowlist, without local user data."""
import argparse
import hashlib
import importlib.metadata as metadata
from pathlib import Path
import sys
import zipfile

ROOT = Path(__file__).resolve().parent
PACKAGES = ('windows-capture', 'openvr', 'Pillow', 'psutil', 'numpy',
            'opencv-python', 'pyinstaller')
DOCUMENTS = ('README.md', 'USAGE.md', 'RELEASE_NOTES.md', 'VALIDATION.md',
             'THIRD_PARTY_NOTICES.md', 'VERSION')


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--app-dir', type=Path, default=ROOT / 'dist' / 'DoubaoVR')
    parser.add_argument('--output', type=Path, default=ROOT / 'releases')
    args = parser.parse_args()
    app_dir = args.app_dir.resolve()
    version = (ROOT / 'VERSION').read_text(encoding='utf-8').strip()
    if not (app_dir / 'DoubaoVR.exe').is_file() or not (app_dir / '_internal').is_dir():
        raise SystemExit('Build dist/DoubaoVR with PyInstaller first.')
    entries = {'DoubaoVR/DoubaoVR.exe': app_dir / 'DoubaoVR.exe'}
    for source in (app_dir / '_internal').rglob('*'):
        if source.is_file():
            relative = source.relative_to(app_dir)
            if any(part.lower() in {'logs', '__pycache__'} for part in relative.parts):
                continue
            if source.name.lower() in {'settings.json', '.env'}:
                continue
            entries[f'DoubaoVR/{relative.as_posix()}'] = source
    for name in DOCUMENTS:
        entries[f'DoubaoVR/{name}'] = ROOT / name
    # Keep each wheel's license tree, including all transitive notices it ships.
    for name in PACKAGES:
        distribution = metadata.distribution(name)
        found = 0
        for relative in distribution.files or ():
            lowered = relative.as_posix().lower()
            if any(token in lowered for token in ('license', 'licence', 'copying')):
                source = Path(distribution.locate_file(relative))
                if source.is_file():
                    if '..' in relative.parts:
                        raise RuntimeError(f'Unexpected license path: {relative}')
                    entries[f'DoubaoVR/licenses/{name}/{relative.as_posix()}'] = source
                    found += 1
        if not found:
            raise RuntimeError(f'Missing bundled license for {name}')
    python_root = Path(sys.base_prefix)
    entries['DoubaoVR/licenses/Python-LICENSE.txt'] = python_root / 'LICENSE.txt'
    entries['DoubaoVR/licenses/OpenVR-LICENSE.txt'] = ROOT / 'licenses' / 'OpenVR-LICENSE.txt'
    for component in ('tcl8.6', 'tk8.6'):
        terms = python_root / 'tcl' / component / 'license.terms'
        if not terms.is_file():
            # Some official Python Windows builds omit Tcl's license file.
            terms = ROOT / 'licenses' / f'{component}-license.terms'
        if not terms.is_file():
            raise RuntimeError(f'Missing Tcl/Tk terms: {terms}')
        entries[f'DoubaoVR/licenses/{component}-license.terms'] = terms
    missing = [str(path) for path in entries.values() if not path.is_file()]
    if missing:
        raise RuntimeError(f'Missing release inputs: {missing}')
    args.output.mkdir(parents=True, exist_ok=True)
    archive = args.output / f'DoubaoVR-v{version}-windows-x64.zip'
    with zipfile.ZipFile(archive, 'w', zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
        for target, source in sorted(entries.items()):
            bundle.write(source, target)
    with archive.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    checksum = archive.with_suffix('.zip.sha256')
    checksum.write_text(f'{digest}  {archive.name}\n', encoding='ascii')
    print(f'{archive}\n{archive.stat().st_size / (1024 ** 2):.1f} MiB, {len(entries)} files\nSHA256 {digest}')


if __name__ == '__main__':
    main()
