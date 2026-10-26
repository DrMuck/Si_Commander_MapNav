# Usage: prefab_fields.py <prefab> <field-regex> [<field-regex> ...]
# Prints every MonoBehaviour whose YAML contains ALL given field names, with its GameObject path and its fields.
import re, sys
path = sys.argv[1]; needles = sys.argv[2:]
text = open(path, encoding='utf-8', errors='ignore').read()
objs = {}
for b in re.split(r'^--- !u!', text, flags=re.M)[1:]:
    m = re.match(r'(\d+) &(-?\d+)', b)
    if not m: continue
    d = {'type': int(m.group(1)), 'raw': b}
    for key in ('m_Name', 'm_GameObject', 'm_Father', 'm_IsActive', 'm_Enabled'):
        mm = re.search(r'^\s*' + key + r': (.*)$', b, re.M)
        if mm: d[key] = mm.group(1).strip()
    objs[m.group(2)] = d
fid = lambda s: (re.search(r'fileID: (-?\d+)', s or '') or [None, None])[1]
tf_of = {fid(d.get('m_GameObject')): k for k, d in objs.items() if d['type'] in (4, 224)}
def path_of(go):
    out = []
    while go and go in objs and len(out) < 25:
        d = objs[go]; out.insert(0, f"{d.get('m_Name')}({d.get('m_IsActive')})")
        tf = tf_of.get(go); fa = fid(objs.get(tf, {}).get('m_Father')) if tf else None
        go = fid(objs.get(fa, {}).get('m_GameObject')) if fa and fa != '0' else None
    return '/'.join(out)
def name_of(f):
    d = objs.get(f)
    if not d: return f
    if d['type'] == 1: return 'GO:' + str(d.get('m_Name'))
    go = fid(d.get('m_GameObject'))
    return f"t{d['type']}@{objs.get(go, {}).get('m_Name')}"
for k, d in objs.items():
    if d['type'] != 114: continue
    if all(re.search(r'^\s+' + n + r':', d['raw'], re.M) for n in needles):
        print('==', k, 'enabled', d.get('m_Enabled'), 'on', path_of(fid(d.get('m_GameObject'))))
        for ln in d['raw'].splitlines()[12:40]:
            s = ln.strip()
            f = fid(s)
            print('    ', s[:150], ('-> ' + name_of(f)) if f and f != '0' else '')
