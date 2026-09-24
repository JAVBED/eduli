"""Build the local game bundle from the supplied MinecraftEdu installers (Python stdlib only)."""
import bz2
import hashlib
import io
import json
from pathlib import Path, PurePosixPath
import re
import struct
import zipfile

ROOT = Path(__file__).resolve().parent.parent


def safe_path(name):
    name = name.replace('\\', '/')
    if name.startswith('/') or ':' in name or '..' in PurePosixPath(name).parts:
        raise ValueError('Unsafe installer path: ' + name)
    return name


def izpack_files(archive):
    # These IzPack 4 installers store bzip2-compressed Java object streams.
    # Extract only PackFile payload block records after a serialized target path.
    for pack in ('packs/pack-pack.tool', 'packs/pack-pack.client'):
        if pack not in archive.namelist():
            continue
        data = bz2.decompress(b'BZ' + archive.read(pack))
        position = 0
        pattern = re.compile(rb't(..)(\$(?:USER_HOME|INSTALL_PATH)[^\x00-\x1f]{1,400})', re.DOTALL)
        while True:
            match = pattern.search(data, position)
            if not match:
                break
            length = struct.unpack('>H', match[1])[0]
            start = match.start(2)
            target = data[start:start + length].decode('utf-8')
            position = start + length
            payload = bytearray()
            while position < len(data) and data[position] in (0x77, 0x7a):
                tag = data[position]
                size = data[position + 1] if tag == 0x77 else struct.unpack_from('>I', data, position + 1)[0]
                position += 2 if tag == 0x77 else 5
                if position + size > len(data):
                    raise ValueError('Truncated IzPack payload')
                payload.extend(data[position:position + size])
                position += size
            if not payload:
                continue
            target = target.replace('\\', '/')
            if '/.minecraft/' in target:
                name = 'minecraft/' + target.split('/.minecraft/', 1)[1]
            elif '/Launcher/' in target:
                name = target.split('/Launcher/', 1)[1]
                if name.startswith('jar/'):
                    name = 'launcher_res/' + name
            else:
                continue
            if payload == b'\x00\x00\x00\x00':
                continue
            if name.endswith('.jar'):
                with zipfile.ZipFile(io.BytesIO(payload)) as jar:
                    if jar.testzip():
                        raise ValueError('Damaged IzPack JAR: ' + name)
            yield safe_path(name), bytes(payload)


def installer_files(path):
    with zipfile.ZipFile(path) as installer:
        if 'install_res/launcher.zip' not in installer.namelist():
            yield from izpack_files(installer)
            return
        with zipfile.ZipFile(io.BytesIO(installer.read('install_res/launcher.zip'))) as launcher:
            for item in launcher.infolist():
                if not item.is_dir():
                    yield safe_path(item.filename), launcher.read(item)
        if 'install_res/forge_libs.zip' in installer.namelist():
            with zipfile.ZipFile(io.BytesIO(installer.read('install_res/forge_libs.zip'))) as forge:
                for item in forge.infolist():
                    if not item.is_dir():
                        name = safe_path(item.filename)
                        yield (name if name.startswith('minecraft/') else 'minecraft/' + name), forge.read(item)


def build():
    installers = sorted((ROOT / 'minecraftedu').rglob('*.jar'))
    if not installers:
        raise SystemExit('No MinecraftEdu installers found')
    output = ROOT / 'dist' / 'minecraftedu.base.zip'
    output.parent.mkdir(exist_ok=True)
    temporary = output.with_suffix('.part')
    versions, seen = [], set()
    try:
        with zipfile.ZipFile(temporary, 'w', compression=zipfile.ZIP_DEFLATED, compresslevel=1, allowZip64=True) as bundle:
            for index, installer in enumerate(installers, 1):
                files = {}
                for name, data in installer_files(installer):
                    digest = hashlib.sha256(data).hexdigest()
                    files[name] = digest
                    if digest not in seen:
                        bundle.writestr('objects/' + digest, data)
                        seen.add(digest)
                dates = re.findall(r'(20\d{6})', installer.stem)
                modern = 'minecraft/versions/mceduforge/mceduforge.json' in files
                legacy_jar = next((name for name in ('launcher_res/jar/minecraftedu.jar', 'launcher_res/jar/minecraftEdu.jar', 'jar/minecraftedu.jar', 'minecraft/bin/minecraft.jar', 'launcher_res/jar/minecraft.jar') if name in files), None)
                if not modern and not legacy_jar:
                    raise ValueError('No MinecraftEdu client found in ' + str(installer))
                versions.append(dict(id=installer.stem, date=dates[-1] if dates else '', files=files,
                                     modern=modern, legacyJar=legacy_jar,
                                     windows=not re.search(r'-(mac|linux)$', installer.stem)))
                print(f'[{index}/{len(installers)}] {installer.stem}: {len(files)} files', flush=True)
            # A few premium installers omit common runtime libraries. Reuse only
            # bin/lib files supplied by a classroom installer for the same game line.
            for version in versions:
                if not version['windows'] or version['modern']:
                    continue
                base = version['id'].split('_', 1)[0]
                donors = [v for v in versions if v['windows'] and 'classroom' in v['id'] and v['id'].split('_', 1)[0] == base]
                if donors:
                    donor = max(donors, key=lambda v: v['date'])
                    for name, digest in donor['files'].items():
                        if (name.startswith('minecraft/bin/') and name != 'minecraft/bin/minecraft.jar') or name.startswith('minecraft/lib/'):
                            version['files'].setdefault(name, digest)
            bundle.writestr('catalog.json', json.dumps(versions, separators=(',', ':')))
        temporary.replace(output)
    finally:
        temporary.unlink(missing_ok=True)
    print(f'Packed {len(versions)} versions, {len(seen)} unique files: {output.stat().st_size / 1024**2:.1f} MiB')


if __name__ == '__main__':
    build()
