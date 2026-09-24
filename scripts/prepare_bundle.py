"""Add missing platform libraries to an installer-derived base bundle."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parent.parent


def allowed(library, os):
    result = 'rules' not in library
    for rule in library.get('rules', []):
        if rule.get('os', {}).get('name', os) == os:
            result = rule['action'] == 'allow'
    return result


def library_path(library, os):
    classifier = library.get('natives', {}).get(os, '').replace('${arch}', '64')
    if not allowed(library, os) or ('natives' in library and not classifier):
        return None
    group, artifact, version, *extra = library['name'].split(':')
    classifier = classifier or (extra[0] if extra else '')
    return f'{group.replace(".", "/")}/{artifact}/{version}/{artifact}-{version}' + ('-' + classifier if classifier else '') + '.jar'


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('source', type=Path)
    parser.add_argument('--output', type=Path, default=ROOT / 'dist' / 'minecraftedu.bundle.zip')
    args = parser.parse_args()
    args.output.parent.mkdir(exist_ok=True, parents=True)
    temporary = args.output.with_suffix('.part')
    shutil.copyfile(args.source, temporary)
    with zipfile.ZipFile(temporary, 'a', zipfile.ZIP_DEFLATED, compresslevel=1) as bundle:
        versions = json.loads(bundle.read('catalog.json'))
        objects = {item.filename for item in bundle.infolist()}
        shared = {name: digest for version in versions for name, digest in version['files'].items()}
        downloads = {}

        def ensure(name, relative):
            if name in shared:
                return shared[name]
            url = 'https://libraries.minecraft.net/' + relative
            print('Adding ' + url, flush=True)
            with urllib.request.urlopen(url, timeout=90) as response:
                data = response.read()
            digest = hashlib.sha256(data).hexdigest()
            obj = 'objects/' + digest
            if obj not in objects:
                bundle.writestr(obj, data)
                objects.add(obj)
            shared[name] = digest
            downloads[name] = {'url': url, 'sha256': digest, 'size': len(data)}
            return digest

        for version in versions:
            files = version['files']
            if version['modern']:
                metadata = json.loads(bundle.read('objects/' + files['minecraft/versions/mceduforge/mceduforge.json']))
                for os in ('windows', 'linux', 'osx'):
                    for library in metadata['libraries']:
                        relative = library_path(library, os)
                        if relative:
                            name = 'minecraft/libraries/' + relative
                            files.setdefault(name, ensure(name, relative))
            else:
                for os in ('linux', 'osx'):
                    libraries = {
                        'lwjgl.jar': 'org/lwjgl/lwjgl/lwjgl/2.9.3/lwjgl-2.9.3.jar',
                        'lwjgl_util.jar': 'org/lwjgl/lwjgl/lwjgl_util/2.9.3/lwjgl_util-2.9.3.jar',
                        'jinput.jar': 'net/java/jinput/jinput/2.0.5/jinput-2.0.5.jar',
                        'lwjgl-natives.jar': f'org/lwjgl/lwjgl/lwjgl-platform/2.9.3/lwjgl-platform-2.9.3-natives-{os}.jar',
                        'jinput-natives.jar': f'net/java/jinput/jinput-platform/2.0.5/jinput-platform-2.0.5-natives-{os}.jar',
                    }
                    for filename, relative in libraries.items():
                        name = f'platform/legacy/{os}/{filename}'
                        files[name] = ensure(name, relative)
        # Avoid duplicate catalog entries: rewrite the ZIP once, copying object data.
    rewritten = args.output.with_suffix('.rewrite')
    with zipfile.ZipFile(temporary) as source, zipfile.ZipFile(rewritten, 'w', zipfile.ZIP_DEFLATED, compresslevel=1) as target:
        for entry in source.infolist():
            if entry.filename != 'catalog.json':
                target.writestr(entry.filename, source.read(entry))
        target.writestr('catalog.json', json.dumps(versions, separators=(',', ':')))
    rewritten.replace(args.output)
    temporary.unlink()
    (ROOT / 'scripts' / 'platform-downloads.json').write_text(json.dumps(downloads, indent=2) + '\n')
    digest = hashlib.file_digest(args.output.open('rb'), 'sha256').hexdigest()
    (ROOT / 'bundle.json').write_text(json.dumps({'asset': args.output.name, 'sha256': digest, 'size': args.output.stat().st_size, 'versions': len(versions)}, indent=2) + '\n')
    print(f'Prepared {len(versions)} versions: {args.output} ({args.output.stat().st_size // 1024**2} MiB)')


if __name__ == '__main__':
    main()
