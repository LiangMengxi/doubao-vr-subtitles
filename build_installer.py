"""Build an Inno Setup installer from the already verified release allowlist."""
import argparse
import hashlib
from pathlib import Path
import re
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parent


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--iscc', type=Path, required=True, help='Path to Inno Setup 6.7+ ISCC.exe')
    args = parser.parse_args()
    version = (ROOT / 'VERSION').read_text(encoding='utf-8').strip()
    if not re.fullmatch(r'\d+\.\d+\.\d+', version):
        raise SystemExit('VERSION must contain three numeric components.')
    archive = ROOT / 'releases' / f'DoubaoVR-v{version}-windows-x64.zip'
    expected = archive.with_suffix('.zip.sha256').read_text().split()[0]
    with archive.open('rb') as stream:
        actual = hashlib.file_digest(stream, 'sha256').hexdigest()
    if actual != expected:
        raise SystemExit('Release archive checksum does not match.')
    staging = ROOT / 'artifacts' / 'installer-payload'
    staging.mkdir(parents=True, exist_ok=True)
    # Keep staging local; a fresh directory avoids mixing files from old builds.
    payload = Path(tempfile.mkdtemp(prefix=f'{version}-', dir=staging))
    with zipfile.ZipFile(archive) as bundle:
        for item in bundle.infolist():
            parts = Path(item.filename).parts
            if (not parts or parts[0] != 'DoubaoVR' or '..' in parts
                    or Path(item.filename).is_absolute()
                    or any(p.lower() in {'logs', 'artifacts', '__pycache__'} for p in parts)
                    or Path(item.filename).name.lower() in {'settings.json', '.env'}):
                raise SystemExit(f'Unexpected installer payload: {item.filename}')
        bundle.extractall(payload)
    subprocess.run([str(args.iscc.resolve()), '/Q', f'/DAppVersion={version}',
                    f'/DPayloadDir={payload / "DoubaoVR"}',
                    f'/DReleaseDir={ROOT / "releases"}',
                    str(ROOT / 'installer' / 'DoubaoVR.iss')], check=True)
    installer = ROOT / 'releases' / f'DoubaoVR-v{version}-Setup-x64.exe'
    with installer.open('rb') as stream:
        digest = hashlib.file_digest(stream, 'sha256').hexdigest()
    installer.with_suffix('.exe.sha256').write_text(f'{digest}  {installer.name}\n', encoding='ascii')
    print(f'{installer}\nSHA256 {digest}')


if __name__ == '__main__':
    main()
