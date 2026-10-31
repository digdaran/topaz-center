"""Recomputes every section of a Topaz aggregated shift export from central `records` and compares.

Usage: python3 -I shift_report.py <shift-export.xml> <records.ndjson>
"""
import sys, json, xml.etree.ElementTree as ET
from decimal import Decimal as D
from datetime import datetime
from collections import defaultdict
export, records_path = sys.argv[1:3]
R = defaultdict(dict)
for line in open(records_path, encoding='utf-8'):
    r = json.loads(line)
    if not r['deleted']: R[r['dataset']][r['key']] = r['data']
def dec(v): return D('0') if v in (None, '') else D(str(v).replace(',', '.'))
def lt(v): return datetime.fromisoformat(v[:19]) if v else None
def xt(v): return datetime.strptime(v, '%d.%m.%Y %H:%M:%S') if v else None
S = ET.parse(export).getroot().find('Sessions/Session')
num = int(S.get('SessionNum'))
ses = next(s for s in R['sysSessions'].values() if s['SessionNum'] == num)
sid = ses['SessionID']
print(f"Смена {num}: SessionID={sid}; начало {xt(S.get('StartDateTime'))} vs {lt(ses['StartDateTime'])}; конец {xt(S.get('EndDateTime'))} vs {lt(ses['EndDateTime'])}; оператор {S.get('UserName')!r} vs {ses['UserName']!r}")
start, end = lt(ses['StartDateTime']), lt(ses['EndDateTime'])
results = []
def check(section, label, exp, got):
    ok = exp == got
    results.append((section, ok))
    if not ok: print(f"  ✗ {section} {label}: Топаз={exp} БД={got}")
    return ok

pm = {p['PaymentModeID']: p for p in R['dcPaymentModes'].values()}
tanks = {t['TankID']: t for t in R['dcTanks'].values()}
hoses = {h['HoseID']: h for h in R['dcHoses'].values()}
fuels = {f['FuelID']: f for f in R['dcFuels'].values()}
agents = {a['AgentID']: a for a in R['dcAgents'].values()}
partners = {p['PartnerID']: p for p in R['dcPartners'].values()}
items = {i['ItemID']: i for i in R['dcItems'].values()}

print('\n[Tanks] ← flSesTanks + dcTanks')
st = {t['TankNum']: t for t in R['flSesTanks'].values() if t['SessionID'] == sid}
for x in S.find('Tanks'):
    t = st.get(int(x.get('TankNum')))
    if t is None: check('Tanks', f"tank {x.get('TankNum')}", 'есть', 'нет'); continue
    dt = tanks[t['TankID']]
    check('Tanks', f"#{t['TankNum']} StartFuelVolume", dec(x.get('StartFuelVolume')), dec(t['StartFuelVolume']))
    check('Tanks', f"#{t['TankNum']} EndFactVolume", x.get('EndFactVolume') or None, t['FactFuelVolume'])
    check('Tanks', f"#{t['TankNum']} DeadRest", dec(x.get('DeadRest')), dec(dt['DeadRest']))
    check('Tanks', f"#{t['TankNum']} DeadRestLiter", dec(x.get('DeadRestLiter')), dec(dt['DeadRestLiter']))
print('  резервуаров в flSesTanks за смену:', len(st), '| в отчёте:', len(S.find('Tanks')))

print('\n[Hoses] ← flSesHoses')
sh = {h['ThroughNumber']: h for h in R['flSesHoses'].values() if h['SessionID'] == sid}
for x in S.find('Hoses'):
    h = sh.get(int(x.get('HoseNum')))
    for a, b in (('StartCounter', 'StartAdder'), ('EndCounter', 'EndAdder'), ('PumpNum', 'PumpNum'), ('NumInPump', 'NumInPump')):
        check('Hoses', a, dec(x.get(a)), dec(h[b]))

orders = sorted((o for o in R['flOrders'].values() if o['SessionID'] == sid), key=lambda o: o['OrderNum'])
def order_key(o):
    return (str(tanks[o['TankID']]['Num']), str(hoses[o['HoseID']]['Num']), fuels[o['FuelID']]['Name'], pm[o['PaymentModeID']]['Name'],
            agents.get(o['AgentID'], {}).get('Name', '?').strip(), o['CardCode'], o['Remark'])
print(f'\n[OutcomesByRetailDetail] ← flOrders ({len(orders)} в БД)')
det = list(S.find('OutcomesByRetailDetail'))
check('Detail', 'количество', len(det), len(orders))
byn = {o['OrderNum']: o for o in orders}
for x in det:
    o = byn[int(x.get('OrderNum'))]
    k = order_key(o)
    for a, got in (('DateTime', lt(o['DateTime'])), ('Volume', dec(o['Volume'])), ('Amount', dec(o['Amount'])), ('OrigPrice', dec(o['Price'])),
                   ('AmountCash', dec(o['AmountCash'])), ('AmountCashless', dec(o['AmountCashless'])), ('Mass', dec(o['Mass'])),
                   ('Density', dec(o['Density'])), ('Temperature', o['Temperature']), ('Remark', o['Remark']), ('CardCode', o['CardCode']),
                   ('TaxStr', o['TaxStr']), ('PaymentModeName', k[3]), ('FuelName', k[2]), ('TankNum', k[0]), ('HoseName', k[1]),
                   ('AgentName', k[4])):
        e = x.get(a)
        e = xt(e) if a == 'DateTime' else dec(e) if isinstance(got, D) else (e or None) if got is None else (e.strip() if a == 'AgentName' else e)
        if a == 'Temperature' and got is None: e = x.get(a) or None
        check('Detail', f"№{o['OrderNum']} {a}", e, got)

print('\n[OutcomesByRetail] ← flOrders, группировка')
agg = defaultdict(lambda: defaultdict(D))
for o in orders:
    g = agg[order_key(o)]
    for f in ('Volume', 'Amount', 'Mass', 'AmountCash', 'AmountCashless', 'AmountBonuses', 'AccruedBonuses'): g[f] += dec(o[f])
    g['OrderCount'] += 1; g['OrigPrice'] = dec(o['Price'])
for x in S.find('OutcomesByRetail'):
    k = (x.get('TankNum'), x.get('HoseName'), x.get('FuelName'), x.get('PaymentModeName'), x.get('AgentName').strip(), x.get('CardCode'), x.get('Remark'))
    g = agg.pop(k, None)
    if g is None: check('Retail', f'группа {k}', 'есть', 'нет'); continue
    for f in ('Volume', 'Amount', 'Mass', 'OrigPrice', 'OrderCount', 'AmountCash', 'AmountCashless', 'AmountBonuses', 'AccruedBonuses'):
        check('Retail', f"{k[3]}/{k[6] or '-'} {f}", dec(x.get(f)), g[f])
if agg: check('Retail', 'лишние группы', [], list(agg))

ft = [h for h in R['trFastTradeH'].values() if h['SessionID'] == sid]
lines = [(h, l) for h in ft for l in R['trFastTradeD'].values() if l['FastTradeHID'] == h['FastTradeHID']]
print(f'\n[ItemOutcomesByRetail / ByPaysheet] ← trFastTradeH ({len(ft)} чеков) + trFastTradeD ({len(lines)} строк)')
def item_key(h, l): return (l['ItemID'], pm[h['PaymentModeID']]['Name'], partners.get(h['PartnerID'], {}).get('Name', '?'), h['IsReturn'])
ia = defaultdict(lambda: defaultdict(D))
for h, l in lines:
    g = ia[item_key(h, l)]
    g['Quantity'] += dec(l['Quantity']); g['Amount'] += dec(l['Total']); g['AmountByBonuses'] += dec(l['AmountBonuses'])
    g['SumPrice'] += dec(l['Price']); g['Lines'] += 1; g['Price'] = dec(l['Price']); g['AmountLine'] += dec(l['Amount'])
for x in S.find('ItemOutcomesByRetail'):
    k = (int(x.get('ItemID')), x.get('PaymentModeName'), x.get('PartnerName'), int(x.get('IsReturn')))
    g = ia.get(k)
    if g is None: check('Items', f'группа {k}', 'есть', 'нет'); continue
    check('Items', f"{x.get('ItemName')}/{k[1]} Quantity", dec(x.get('Quantity')), g['Quantity'])
    check('Items', f"{x.get('ItemName')}/{k[1]} Amount", dec(x.get('Amount')), g['Amount'])
    check('Items', f"{x.get('ItemName')}/{k[1]} PriceFastTrade", dec(x.get('PriceFastTrade')), g['Price'])
    check('Items', f"{x.get('ItemName')} ItemName=dcItems.Name", x.get('ItemName'), items[k[0]]['Name'])
    check('Items', f"{x.get('ItemName')} ItemFullName=dcItems.FullName", x.get('ItemFullName'), items[k[0]]['FullName'])
    print(f"    {x.get('ItemName')[:22]:22s}/{k[1][:10]:10s} PriceIn={x.get('PriceIn')} PriceRetail={x.get('PriceRetail')} PriceFastTrade={x.get('PriceFastTrade')} AmountByBonuses={x.get('AmountByBonuses')}")
for x in S.find('ItemOutcomesByPaysheet'):
    k = (int(x.get('ItemID')), x.get('PaymentModeName'), x.get('PartnerName'), int(x.get('IsReturn')))
    g = ia.get(k)
    check('Paysheet', f"{x.get('ItemName')}/{k[1]} Quantity", dec(x.get('Quantity')), g['Quantity'])
    check('Paysheet', f"{x.get('ItemName')}/{k[1]} Amount", dec(x.get('Amount')), g['Amount'])
    check('Paysheet', f"{x.get('ItemName')}/{k[1]} Price=SUM(trFastTradeD.Price)", dec(x.get('Price')), g['SumPrice'])

print('\n[CashFlow] ← rgCashes')
ca = defaultdict(D)
for c in R['rgCashes'].values():
    if c['SessionID'] == sid: ca[(c['DocTypeID'], c['CashID'])] += dec(c['Amount'])
for x in S.find('CashFlow'):
    k = (int(x.get('DocTypeID')), int(x.get('CashID')))
    check('Cash', f"{x.get('DocTypeName')}/касса {k[1]}", dec(x.get('Amount')), ca.pop(k, None))
if ca: check('Cash', 'лишние', {}, dict(ca))
rem = [c['Remark'] for t in ('cmCashReceipt', 'cmCashOut') for c in R[t].values() if c['SessionID'] == sid]
print('    Remark в cmCashReceipt/cmCashOut за смену:', rem)

print('\n[TradeDocsOutActs] ← trOutActH/D')
for x in S.find('TradeDocsOutActs'):
    h = R['trOutActH'][x.get('OutActHID')]
    check('OutActs', f"акт {x.get('NDoc')} DateDoc", xt(x.get('DateDoc')), lt(h['DateDoc']))
    dl = [l for l in R['trOutActD'].values() if l['OutActHID'] == h['OutActHID']]
    check('OutActs', f"акт {x.get('NDoc')} строки", sorted((i.get('ItemName'), dec(i.get('Quantity'))) for i in x),
          sorted((items[l['ItemID']]['Name'], dec(l['Quantity'])) for l in dl))
acts = [h for h in R['trOutActH'].values() if start <= lt(h['DateDoc']) <= end]
print('    актов списания в интервале смены по DateDoc:', len(acts))

print('\n[ItemRests] ← SUM(rgItemRests.Quantity)')
variants = {'на конец смены': lambda r: lt(r['Date']) <= end, 'на момент экспорта (всё)': lambda r: True}
by_name = {i['Name']: i['ItemID'] for i in items.values()}
for vname, cond in variants.items():
    rest = defaultdict(D)
    for r in R['rgItemRests'].values():
        if cond(r): rest[r['ItemID']] += dec(r['Quantity'])
    bad = [(x.get('ItemName'), x.get('ItemRest'), rest.get(by_name[x.get('ItemName')])) for x in S.find('ItemRests') if dec(x.get('ItemRest')) != rest.get(by_name[x.get('ItemName')], D(0))]
    print(f"    {vname}: совпало {len(S.find('ItemRests')) - len(bad)}/{len(S.find('ItemRests'))}", bad[:5])
    nonzero = sum(1 for v in rest.values() if v != 0)
    print(f"      позиций с ненулевым остатком в БД: {nonzero}")

tot = defaultdict(lambda: [0, 0])
for s, ok in results: tot[s][0 if ok else 1] += 1
print('\nИТОГО:', {s: f'{a} ok / {b} расх.' for s, (a, b) in tot.items()})
