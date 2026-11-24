import json, urllib.request, urllib.parse, sys, os, collections
here = os.path.dirname(os.path.abspath(__file__)); sys.path.insert(0, here)
from mixmap import MIX
from iconmap import MAP
print('unmapped:', [hex(c) for c in MAP if c not in MIX])
def get(url): return json.load(urllib.request.urlopen(urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0 Ivo'})))
by = collections.defaultdict(set)
for v in MIX.values(): p, n = v.split(':'); by[p].add(n)
out = {}
for p, names in by.items():
    d = get(f'https://api.iconify.design/{p}.json?icons=' + ','.join(sorted(names)))
    if not isinstance(d, dict): print('BAD PREFIX', p); continue
    for n in d.get('not_found', []):
        q = n.replace('-outline', '').replace('-line', '').split('-')[0]
        s = get(f'https://api.iconify.design/search?query={urllib.parse.quote(q)}&prefix={p}&limit=12')
        print(f'MISSING {p}:{n}  ->', [i.split(':')[1] for i in s.get('icons', [])])
    for n, ic in d.get('icons', {}).items():
        out[f'{p}:{n}'] = {'body': ic['body'], 'w': ic.get('width', d.get('width', 24)), 'h': ic.get('height', d.get('height', 24)), 'left': ic.get('left', d.get('left', 0)), 'top': ic.get('top', d.get('top', 0))}
    for a, al in d.get('aliases', {}).items():
        par = d['icons'][al['parent']]
        out[f'{p}:{a}'] = {'body': par['body'], 'w': par.get('width', d.get('width', 24)), 'h': par.get('height', d.get('height', 24)), 'left': par.get('left', d.get('left', 0)), 'top': par.get('top', d.get('top', 0))}
json.dump(out, open(os.path.join(here, 'mix.json'), 'w'))
print('got', len(out))


