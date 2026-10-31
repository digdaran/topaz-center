"""Checks that the central SQL views (v_shift_*) reproduce a Topaz aggregated shift export.

Usage: python3 -I shift_views.py <shift-export.xml> <views.json>
"""
import sys, json, xml.etree.ElementTree as ET
from decimal import Decimal as D
S = ET.parse(sys.argv[1]).getroot().find('Sessions/Session'); V = json.load(open(sys.argv[2]))
d = lambda v: D(str(v or 0).replace(',', '.'))
ok = bad = 0
def chk(label, a, b):
    global ok, bad
    if a == b: ok += 1
    else: bad += 1; print('  ✗', label, 'Топаз', a, 'view', b)
fuel = {(str(v['tank_num']), str(v['hose_num']), v['fuel_name'], v['payment_mode'], v['remark'] or ''): v for v in V['fuel']}
for x in S.find('OutcomesByRetail'):
    v = fuel.pop((x.get('TankNum'), x.get('HoseName'), x.get('FuelName'), x.get('PaymentModeName'), x.get('Remark')), None)
    if not v: chk('группа', x.attrib, None); continue
    for a, b in (('Volume','volume'),('Amount','amount'),('Mass','mass'),('OrigPrice','price'),('OrderCount','order_count'),('AmountCash','amount_cash'),('AmountCashless','amount_cashless'),('AmountBonuses','amount_bonuses')):
        chk(f"fuel {x.get('PaymentModeName')}/{x.get('Remark')} {a}", d(x.get(a)), d(v[b]))
chk('лишние группы топлива', {}, fuel)
cash = {(v['doc_type_id'], v['cash_id']): v for v in V['cash']}
for x in S.find('CashFlow'):
    v = cash.pop((int(x.get('DocTypeID')), int(x.get('CashID'))), None)
    chk(f"cash {x.get('DocTypeName')}/{x.get('CashName').strip()}", d(x.get('Amount')), d(v and v['amount']))
    chk(f"cash names {x.get('DocTypeID')}", (x.get('DocTypeName'), x.get('CashName')), (v and v['doc_type'], v and v['cash_name']))
chk('лишние строки кассы', {}, cash)
items = {(v['item_id'], v['payment_mode'], v['partner_name'], v['is_return']): v for v in V['items']}
for x in S.find('ItemOutcomesByRetail'):
    v = items.pop((int(x.get('ItemID')), x.get('PaymentModeName'), x.get('PartnerName'), x.get('IsReturn') == '1'), None)
    for a, b in (('Quantity','quantity'),('Amount','amount'),('PriceFastTrade','price')): chk(f"item {x.get('ItemName')}/{x.get('PaymentModeName')} {a}", d(x.get(a)), d(v and v[b]))
chk('лишние товары', {}, items)
rests = {v['item_name']: v for v in V['rests']}
for x in S.find('ItemRests'):
    v = rests.pop(x.get('ItemName'), None); chk(f"rest {x.get('ItemName')}", d(x.get('ItemRest')), d(v and v['quantity']))
chk('лишние остатки', {}, rests)
t = V['totals']
hose = S.find('Hoses/Hose'); chk('totals hose delta', d(hose.get('EndCounter')) - d(hose.get('StartCounter')), d(t['hose_counters_delta']))
chk('totals fuel volume', sum(d(x.get('Volume')) for x in S.find('OutcomesByRetail')), d(t['fuel_volume']))
print(f'views vs Топаз: {ok} ok, {bad} расхождений'); print('totals:', t)
