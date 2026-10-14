// Run with:  node --test dust/test/*.test.js
const test = require('node:test');
const assert = require('node:assert');
const L = require('../log.js');

function goodDay() {
  const site = L.newSite({ permitNo: 'E123456', projectName: 'Pointe Grand Mesa', permitExpires: '2027-01-01' });
  const log = L.newLog(site, '2026-10-14', {
    preparedBy: 'Pat Doe', activities: 'Sewer trenching', windMph: '8'
  });
  for (const t of ['07:00', '12:00']) {
    const c = L.newCheck(site, t, 'Pat Doe');
    site.items.forEach(i => { c.status[i.id] = 'Y'; });
    log.checks.push(c);
  }
  log.water.push({ time: '06:45', source: 'WT-2', loads: '2', gallons: '4000', area: 'Trench line' });
  return { site, log };
}

test('a complete day has no errors and can be signed', () => {
  const { site, log } = goodDay();
  const v = L.validate(site, log);
  assert.deepStrictEqual(v.errors, []);
  L.sign(site, log, 'Pat Doe', 'data:image/png;base64,xx', new Date('2026-10-14T23:00:00Z'));
  assert.strictEqual(log.signedAt, '2026-10-14T23:00:00.000Z');
  assert.throws(() => L.sign(site, log, 'Pat Doe'), /already signed/);
});

test('missing permit, inspections and activities are errors', () => {
  const site = L.newSite();
  const log = L.newLog(site, '2026-10-14', { preparedBy: 'X' });
  const e = L.validate(site, log).errors.join('\n');
  assert.match(e, /permit number/);
  assert.match(e, /No visual inspections/);
  assert.match(e, /dust-generating work/);
  assert.strictEqual(L.canSign(site, log), false);
});

test('an item marked N needs a comment or corrective action', () => {
  const { site, log } = goodDay();
  log.checks[0].status.trackout = 'N';
  assert.match(L.validate(site, log).errors.join('\n'), /marked N/);
  log.actions.push({ itemId: 'trackout', issue: 'Mud on 7th Ave', action: 'Swept', fixedAt: '07:30' });
  assert.deepStrictEqual(L.validate(site, log).errors, []);
  log.actions = [];
  log.checks[0].comments.trackout = 'Swept with skid steer broom 07:20';
  assert.deepStrictEqual(L.validate(site, log).errors, []);
});

test('blank items are errors', () => {
  const { site, log } = goodDay();
  log.checks[1].status.piles = '';
  assert.match(L.validate(site, log).errors.join('\n'), /Check 2 \(12:00\): 1 item/);
});

test('water marked Y requires a water entry with gallons', () => {
  const { site, log } = goodDay();
  log.water = [];
  assert.match(L.validate(site, log).errors.join('\n'), /no water application is logged/);
  log.water.push({ time: '07:00', gallons: '' });
  assert.match(L.validate(site, log).errors.join('\n'), /gallons is missing/);
  assert.strictEqual(L.totalWater({ water: [{ gallons: '4000' }, { gallons: 2500.5 }, { gallons: 'x' }] }), 6500.5);
});

test('expired permit is an error, high wind is a warning', () => {
  const { site, log } = goodDay();
  site.permitExpires = '2026-10-01';
  log.gustMph = '30';
  const v = L.validate(site, log);
  assert.match(v.errors.join('\n'), /Permit expired/);
  assert.match(v.warnings.join('\n'), /High wind/);
});

test('no-work day only needs the header', () => {
  const site = L.newSite({ permitNo: 'E1' });
  const log = L.newLog(site, '2026-10-14', { preparedBy: 'Pat', noWork: true, notes: 'Rain day' });
  assert.deepStrictEqual(L.validate(site, log), { errors: [], warnings: [] });
});

test('amendments only on signed logs', () => {
  const { site, log } = goodDay();
  assert.throws(() => L.amend(log, 'Pat', 'fix'), /Only signed/);
  L.sign(site, log, 'Pat Doe');
  L.amend(log, 'Pat Doe', 'Water truck was WT-3, not WT-2');
  assert.strictEqual(log.amendments.length, 1);
  assert.throws(() => L.amend(log, '', 'x'), /name and a note/);
});

test('CSV has one row per check item and escapes formulas', () => {
  const { site, log } = goodDay();
  log.checks[0].comments.water = '=HYPERLINK("x")';
  const csv = L.toCSV(site, [log]);
  const lines = csv.split('\r\n');
  assert.strictEqual(lines.length, 1 + 2 * site.items.length);
  assert.ok(csv.includes(`"'=HYPERLINK(""x"")"`));
  assert.ok(lines[1].includes('"4000"'));
});

test('merge keeps all records and prefers signed versions', () => {
  const { site, log } = goodDay();
  const store = L.emptyStore();
  store.sites.push(site); store.logs.push(log);
  const signed = JSON.parse(JSON.stringify(log));
  L.sign(site, signed, 'Pat Doe');
  const other = L.newLog(site, '2026-10-15', { preparedBy: 'Pat' });
  const backup = L.parseBackup(JSON.stringify({ sites: [site], logs: [signed, other] }));
  const merged = L.mergeStore(store, backup);
  assert.strictEqual(merged.logs.length, 2);
  assert.ok(merged.logs.find(l => l.id === log.id).signedAt);
  assert.strictEqual(merged.sites.length, 1);
  assert.throws(() => L.parseBackup('{"a":1}'), /Not a Rule 310/);
});
