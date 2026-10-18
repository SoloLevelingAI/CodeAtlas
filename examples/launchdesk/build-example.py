"""Build a traceable reading example from an immutable local Git commit.

Only the Atlas report receives manual reading labels. Target source, relations,
raw diagnostics and declaration coverage remain unchanged. No remote operations.
"""
from pathlib import Path
import argparse
import copy
import hashlib
import io
import json
import subprocess
import zipfile

ROOT = Path(__file__).resolve().parents[2]
parser = argparse.ArgumentParser()
parser.add_argument('--repository', required=True)
parser.add_argument('--commit', default='HEAD')
parser.add_argument('--extract-only', action='store_true')
args = parser.parse_args()
repository = Path(args.repository).resolve()
commit = subprocess.check_output(['git', '-C', str(repository), 'rev-parse', args.commit + '^{commit}'], text=True).strip()
snapshot = ROOT / 'artifacts/examples' / ('LaunchDesk-' + commit[:12])
snapshot.mkdir(parents=True, exist_ok=True)
archive = subprocess.check_output(['git', '-C', str(repository), 'archive', '--format=zip', commit])
extensions = {'.cs', '.xaml', '.csproj', '.props', '.slnx', '.ps1'}
hashes = {}
with zipfile.ZipFile(io.BytesIO(archive)) as package:
    for entry in package.infolist():
        if entry.is_dir() or Path(entry.filename).suffix not in extensions:
            continue
        destination = (snapshot / entry.filename).resolve()
        if not destination.is_relative_to(snapshot.resolve()):
            raise ValueError('Unsafe archive path: ' + entry.filename)
        raw = package.read(entry)
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(raw)
        hashes[entry.filename] = hashlib.sha256(raw).hexdigest()
provenance = {'repository': 'https://github.com/SoloLevelingAI/LaunchDesk', 'commit': commit,
              'snapshot': str(snapshot), 'files': hashes,
              'basis': 'Pinned local Git commit; excludes uncommitted prior prototypes. No upstream source modified.'}
(snapshot.parent / 'launchdesk-provenance.json').write_text(json.dumps(provenance, ensure_ascii=False, indent=2), encoding='utf-8')
if args.extract_only:
    print(snapshot)
    raise SystemExit(0)
cli = ROOT / 'artifacts/validation/bin/CodeAtlas.Cli/debug/CodeAtlas.Cli.exe'
reports = ROOT / 'artifacts/reports'
result = subprocess.run([str(cli), 'analyze', '--root', str(snapshot), '--id', 'launchdesk-raw',
                         '--name', 'LaunchDesk · 原始分析', '--reports', str(reports)], capture_output=True, text=True)
if result.returncode not in (0, 2):
    raise RuntimeError(result.stdout + result.stderr)
raw = json.loads((reports / 'launchdesk-raw.json').read_text(encoding='utf-8-sig'))
report = copy.deepcopy(raw)
nodes = report['Graph']['Nodes']
manifest = json.loads((Path(__file__).parent / 'reading-map.json').read_text(encoding='utf-8'))
by_id = {n['Id']: n for n in nodes}
def match(selector):
    found = [n for n in nodes if n['Source']['Path'] == selector['path'] and n['Name'] == selector['name']
             and ('kind' not in selector or n['Kind'] == selector['kind'])]
    if len(found) != 1:
        raise ValueError(f'Selector must match exactly once: {selector}; matches={len(found)}')
    return found[0]
features = []
for feature in manifest['features']:
    evidence = match(feature['evidence'])
    features.append({'Id': feature['id'], 'Name': feature['name'], 'Purpose': '人工整理 · ' + feature['purpose'],
                     'ParentId': feature.get('parent'), 'Kind': 'Capability' if feature.get('parent') else 'Module',
                     'Inputs': '', 'Outputs': '', 'Source': evidence['Source']})
applied = []
for mapping in manifest['mappings']:
    target = match(mapping['selector'])
    targets = [target]
    if mapping.get('children'):
        pending = {target['Id']}
        while pending:
            found = [n for n in nodes if n['ParentId'] in pending]
            targets.extend(found)
            pending = {n['Id'] for n in found}
    for node in targets:
        node['Feature'] = mapping['feature']
        node['Role'] = mapping.get('role')
        node['DeclarationBasis'] = '人工阅读归属 · 原代码未声明；不是自动识别或已完成解耦。'
    if mapping.get('summary'):
        target['Summary'] = '人工说明 · ' + mapping['summary']
    for key in ('Inputs', 'Outputs'):
        if mapping.get(key):
            target[key] = '人工说明 · ' + mapping[key]
    applied.append({'id': target['Id'], 'source': target['Source'], 'feature': mapping['feature'], 'count': len(targets)})
report['Id'] = 'launchdesk-reading'
report['Name'] = 'LaunchDesk · ' + commit[:7] + ' · 人工功能归属'
report['Graph']['Features'] = features
report['Support'] += [
    {'Capability': '功能归属与用途', 'Status': '人工整理', 'Detail': '基于固定提交的真实符号；reading-map.json 记录人工映射，保留原始覆盖与全部诊断。不是源码自声明或自动重构。'},
    {'Capability': 'AI 管理 / PmlTrigger 集成', 'Status': '此提交未发现', 'Detail': '原始提交未发现可作为已实现模块展示的集成。既有临时原型未纳入本例。'},
    {'Capability': '插件拆分与热卸载', 'Status': '未实现验证', 'Detail': '阅读分组不是 DLL 拆分；LaunchService 与 MainViewModel 仍存在职责混合。'}]
for issue in manifest['issues']:
    evidence = match(issue['evidence'])
    report['Findings'].append({'Rule': issue['rule'], 'Severity': 'Warning', 'Message': '人工审阅 · ' + issue['message'], 'Source': evidence['Source'], 'NodeId': evidence['Id']})
assert report['Coverage'] == raw['Coverage']
assert report['Graph']['Relations'] == raw['Graph']['Relations']
assert report['Graph']['Documents'] == raw['Graph']['Documents']
assert len(nodes) == len(raw['Graph']['Nodes'])
assert all(a['Source'] == b['Source'] for a, b in zip(nodes, raw['Graph']['Nodes']))
(reports / 'launchdesk-reading.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
(snapshot.parent / 'launchdesk-mapping-evidence.json').write_text(json.dumps(applied, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'commit': commit, 'nodes': len(nodes), 'mappedSymbols': sum(n['DeclarationBasis'].startswith('人工') for n in nodes),
                  'rawCoveragePreserved': True, 'relationsPreserved': True, 'report': str(reports / 'launchdesk-reading.json')}, ensure_ascii=False))
