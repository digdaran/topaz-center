"""Row-by-row check of a Topaz period export (DataPacket XML) against central `records`.

Usage: python3 -I period_export.py <export.xml> <records.ndjson> <datasets.json> <details-out.json>
"""
import sys, json, re, xml.etree.ElementTree as ET
from decimal import Decimal, InvalidOperation
from datetime import datetime
from collections import defaultdict, Counter
export, records_path, catalog_path = sys.argv[1:4]

recs = defaultdict(dict)
for line in open(records_path, encoding='utf-8'):
    r = json.loads(line); recs[r['dataset']][r['key']] = r['data']
catalog = {d['table']: d for d in json.load(open(catalog_path, encoding='utf-8'))['datasets']}

def norm_export(v, ftype):
    if v is None or v == '': return None
    if ftype in ('INT64','LONG','SHORT','DOUBLE','FLOAT','INTEGER'):
        try: return Decimal(v.replace(',', '.'))
        except InvalidOperation: return v
    if ftype in ('TIMESTAMP','DATE'):
        for f in ('%d.%m.%Y %H:%M:%S', '%d.%m.%Y'):
            try: return datetime.strptime(v, f)
            except ValueError: pass
        return v
    return v
def norm_db(v, ftype):
    if v is None or v == '': return None
    if ftype in ('INT64','LONG','SHORT','DOUBLE','FLOAT','INTEGER'):
        return Decimal(str(v))
    if ftype in ('TIMESTAMP','DATE'):
        s = str(v)
        if 'T' in s: return datetime.fromisoformat(s[:19])        # local wall clock, drop fraction+offset
        return datetime.strptime(s, '%Y-%m-%d')
    return v

root = ET.parse(export).getroot()
summary = []
details = defaultdict(list)
for t in root.find('Tables'):
    name = t.tag
    types = {f.get('Name'): f.get('Type') for f in t.find('Metadata/Fields')}
    rows = list(t.find('Rows'))
    d = catalog.get(name)
    if d is None:
        summary.append((name, len(rows), 'NOT IN CATALOG', '', '', '')); continue
    keycols = d['key']; excl = set(d.get('exclude', []))
    db = recs.get(name, {})
    missing = 0; mism = Counter(); extra_cols = set(); ok = 0
    exported_keys = set()
    for row in rows:
        vals = {c.tag: c.text for c in row}
        key = '|'.join(str(int(Decimal(vals[k]))) if types.get(k) in ('LONG','INT64','SHORT') else vals[k] for k in keycols)
        exported_keys.add(key)
        rec = db.get(key)
        if rec is None:
            missing += 1; details[name].append(f'missing key {key}'); continue
        bad = False
        for col, v in vals.items():
            if col in excl: continue
            if col not in rec: extra_cols.add(col); continue
            a, b = norm_export(v, types.get(col)), norm_db(rec[col], types.get(col))
            if types.get(col) == 'BLOB' and a and b:
                a, b = a.replace('\r\n','\n').strip(), b.replace('\r\n','\n').strip()
            if a != b:
                mism[col] += 1; bad = True
                if len(details[name]) < 400: details[name].append(f'{key}.{col}: export={str(a)[:60]!r} db={str(b)[:60]!r}')
        ok += not bad
    summary.append((name, len(rows), ok, missing, dict(mism), sorted(extra_cols)))
    details[name+'#keys'] = sorted(exported_keys, key=lambda k: [int(x) if x.lstrip('-').isdigit() else x for x in k.split('|')])

print(f"{'table':22s} {'rows':>5s} {'exact':>5s} {'miss':>4s}  mismatching columns / columns absent in DB")
for s in summary: print(f"{s[0]:22s} {s[1]:5d} {str(s[2]):>5s} {str(s[3]):>4s}  {s[4]} {s[5] or ''}")
json.dump(details, open(sys.argv[4], 'w', encoding='utf-8'), ensure_ascii=False, indent=0)
