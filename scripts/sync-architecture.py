"""Compile the authoritative Markdown rules into the GUI/CLI resource; --check is read-only."""
from pathlib import Path
import argparse
import json
import re

root = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--check', action='store_true')
args = parser.parse_args()
folder = root / 'docs/standards'

def sections(text):
    matches = list(re.finditer(r'^## (.+)$', text, re.MULTILINE))
    return {match.group(1): text[match.end():matches[i + 1].start() if i + 1 < len(matches) else len(text)].strip()
            for i, match in enumerate(matches)}

index = sections((folder / 'index.md').read_text(encoding='utf-8-sig'))
data = {'Version': index['版本'], 'Intent': index['目标'], 'Rules': [],
        'Migration': [line.strip() for line in index['整理路线'].splitlines() if line.strip()]}
fields = {'Requirement': '规范要求', 'Reason': '为什么这样分', 'Automation': '自动检查范围',
          'Change': '整理建议', 'Acceptance': '验收条件'}
for path in sorted(folder.glob('A[0-9][0-9].md')):
    text = path.read_text(encoding='utf-8-sig')
    heading = re.match(r'# (A\d{2}) · (.+)', text)
    if not heading or heading.group(1) != path.stem:
        raise ValueError('Invalid rule heading: ' + str(path))
    parts = sections(text)
    rule = {'Id': heading.group(1), 'Title': heading.group(2), 'MarkdownPath': path.relative_to(root).as_posix()}
    rule.update({key: parts[heading] for key, heading in fields.items()})
    rule['FindingRules'] = [] if parts['关联诊断'] == '无自动诊断规则' else [x.strip() for x in parts['关联诊断'].split(',')]
    if not all(rule[key] for key in fields):
        raise ValueError('Incomplete rule: ' + str(path))
    data['Rules'].append(rule)
if [r['Id'] for r in data['Rules']] != [f'A{i:02}' for i in range(1, 11)]:
    raise ValueError('Expected complete A01-A10 rule set')
output = root / 'docs/architecture-standard.v1.json'
generated = json.dumps(data, ensure_ascii=False, indent=2) + '\n'
if args.check:
    if output.read_text(encoding='utf-8-sig') != generated:
        raise SystemExit('Markdown/resource mismatch. Run python scripts/sync-architecture.py before building.')
    print('PASS: Markdown rules and application resource match.')
else:
    output.write_text(generated, encoding='utf-8')
    print('Generated docs/architecture-standard.v1.json from Markdown.')
