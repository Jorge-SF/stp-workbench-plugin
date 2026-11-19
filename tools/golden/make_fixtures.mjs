// Builds synthetic `vgs-stp-snapshot/2` fixtures from compact specs.
//
// Everything here is invented: no client project data. The output mirrors the
// shape produced by `stp_v2_snapshot_contract.build_canonical_snapshot` in the
// Python core (daily civil fragments, native anchors between consecutive bars,
// precedences with lagMicroseconds) closely enough to drive planner_logic.js.
//
// Usage: node tools/golden/make_fixtures.mjs   (rewrites fixtures/*.snapshot.json)

import { createHash } from 'node:crypto';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const OUT = join(HERE, 'fixtures');

// ---- civil time helpers (naive local datetimes, ISO without zone) ----------
const pad = (n, w = 2) => String(n).padStart(w, '0');
const parse = s => {
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})$/.exec(s);
  if (!m) throw new Error(`bad civil datetime ${s}`);
  return Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +m[6]);
};
const fmt = ms => {
  const d = new Date(ms);
  return `${d.getUTCFullYear()}-${pad(d.getUTCMonth() + 1)}-${pad(d.getUTCDate())}T${pad(d.getUTCHours())}:${pad(d.getUTCMinutes())}:${pad(d.getUTCSeconds())}`;
};
const DAY = 86_400_000;
const nextMidnight = ms => Math.floor(ms / DAY) * DAY + DAY;

// Decimal percentages handled as integer ten-thousandths to stay exact.
const toUnits = s => Math.round(Number(s) * 10000);
const fromUnits = u => {
  const whole = Math.trunc(u / 10000), frac = Math.abs(u % 10000);
  return frac ? `${whole}.${pad(frac, 4).replace(/0+$/, '')}` : String(whole);
};

function splitBar(activityId, bar, lockedCompleted) {
  const start = parse(bar.start), finish = parse(bar.finish);
  const edges = [start];
  for (let t = nextMidnight(start); t < finish; t += DAY) edges.push(t);
  edges.push(finish);
  const spans = edges.slice(0, -1).map((s, i) => [s, edges[i + 1]]);
  const total = finish - start, qty = toUnits(bar.quantityPercentage);
  let used = 0;
  return spans.map(([s, f], i) => {
    const q = i === spans.length - 1 ? qty - used : Math.floor(qty * (f - s) / total);
    used += q;
    return {
      id: `${activityId}:${bar.xmlKey}:${bar.key}:${i}:${fmt(s)}:${fmt(f)}`,
      nativeBarXmlKey: bar.xmlKey,
      nativeBarKey: bar.key,
      start: fmt(s),
      finish: fmt(f),
      originalStart: fmt(s),
      originalFinish: fmt(f),
      logicalUnitDate: fmt(s).slice(0, 10),
      displaySuppressed: false,
      quantityPercentage: fromUnits(q),
      lockedCompleted,
    };
  });
}

const linkPayload = l => ({
  key: l.key, predecessorId: l.pred, successorId: l.succ,
  predecessorBar: l.predBar ?? '1', successorBar: l.succBar ?? '1',
  type: l.type ?? 'FinishStart', lagMicroseconds: String(BigInt(l.lagHours ?? 0) * 3_600_000_000n),
  groupKey: l.groupKey ?? '',
});

function anchors(activity, links) {
  const out = [];
  let cumulative = 0;
  activity.bars.forEach((left, i) => {
    const right = activity.bars[i + 1];
    if (!right) return;
    cumulative += toUnits(left.quantityPercentage);
    const refs = [];
    for (const l of links) {
      if (l.predecessorId === activity.id && [left.key, right.key].includes(l.predecessorBar)) refs.push({ key: l.key, role: 'predecessor', endpointBar: l.predecessorBar, otherActivityId: l.successorId, otherBar: l.successorBar, type: l.type, lagMicroseconds: l.lagMicroseconds, groupKey: l.groupKey });
      if (l.successorId === activity.id && [left.key, right.key].includes(l.successorBar)) refs.push({ key: l.key, role: 'successor', endpointBar: l.successorBar, otherActivityId: l.predecessorId, otherBar: l.predecessorBar, type: l.type, lagMicroseconds: l.lagMicroseconds, groupKey: l.groupKey });
    }
    refs.sort((a, b) => (a.key + a.role + a.endpointBar).localeCompare(b.key + b.role + b.endpointBar));
    out.push({
      leftBarXmlKey: left.xmlKey, leftBarKey: left.key, rightBarXmlKey: right.xmlKey, rightBarKey: right.key,
      at: left.finish, rightAt: right.start, contiguous: left.finish === right.start,
      cumulativeQuantityPercentage: fromUnits(cumulative), links: refs,
    });
  });
  return out;
}

const sha = text => createHash('sha256').update(text, 'utf8').digest('hex');

function buildSnapshot(spec) {
  const precedences = spec.links.map(linkPayload);
  const activities = spec.activities.slice().sort((a, b) => a.id.localeCompare(b.id)).map(a => {
    const bars = a.bars.map((b, i) => ({
      xmlKey: b.xmlKey ?? `x${i + 1}`, key: b.key ?? String(i + 1), start: b.start, finish: b.finish,
      quantityPercentage: b.quantityPercentage ?? '100', percentComplete: b.completed ? '100' : '0',
      completeWorkedMinutes: b.completed ? String((parse(b.finish) - parse(b.start)) / 60000) : '0',
    }));
    const activity = { id: a.id, bars };
    const completedQty = a.bars.reduce((s, b, i) => s + (b.completed ? toUnits(bars[i].quantityPercentage) : 0), 0);
    return {
      id: a.id,
      collectionKey: a.collectionKey ?? `c-${a.id}`,
      overallPercentComplete: fromUnits(completedQty),
      grid: { ID: a.id, Description: a.description, Resource: a.resource, Type: a.type },
      nativeBars: bars,
      fragments: bars.flatMap((b, i) => splitBar(a.id, b, Boolean(a.bars[i].completed))),
      nativeAnchors: anchors(activity, precedences),
    };
  });
  const body = { activities, precedences };
  return {
    schema: 'vgs-stp-snapshot/2',
    projectName: `${spec.name}.vgantt`,
    projectPath: `X:\\synthetic\\${spec.name}.vgantt`,
    projectKey: sha(`x:\\synthetic\\${spec.name}.vgantt`),
    // Synthetic fingerprints: deterministic hashes of the fixture body.
    projectFingerprint: sha(JSON.stringify(body)),
    precedenceFingerprint: sha(JSON.stringify(precedences)),
    exportedAt: '2026-03-01T06:00:00',
    dateFormat: 'dd/MM/yyyy',
    activities,
    precedences,
  };
}

// ---- fixture specs ----------------------------------------------------------
const SPECS = [
  {
    // A -> B -> C finish-start chain. C has 12 h of slack after B so a one-day
    // push of B needs ceil(12h) = 1 day on C; D is independent.
    name: 'chain_fs',
    activities: [
      { id: 'A100', description: 'Desarrollo rampa N1', resource: 'Jumbo 01', type: 'Desarrollo', bars: [{ start: '2026-03-02T08:00:00', finish: '2026-03-04T20:00:00' }] },
      { id: 'B200', description: 'Fortificación rampa N1', resource: 'Empernador 01', type: 'Fortificación', bars: [{ start: '2026-03-04T20:00:00', finish: '2026-03-06T20:00:00' }] },
      { id: 'C300', description: 'Shotcrete rampa N1', resource: 'Robot 01', type: 'Shotcrete', bars: [{ start: '2026-03-07T08:00:00', finish: '2026-03-08T20:00:00' }] },
      { id: 'D400', description: 'Desarrollo XC-2', resource: 'Jumbo 02', type: 'Desarrollo', bars: [{ start: '2026-03-03T00:00:00', finish: '2026-03-05T00:00:00' }] },
    ],
    links: [
      { key: 'L1', pred: 'A100', succ: 'B200' },
      { key: 'L2', pred: 'B200', succ: 'C300' },
    ],
  },
  {
    // E500 has a completed first bar (locked) and a pending second bar; F600 is a
    // fully completed successor that G700 would push (StartStart, lag -24 h).
    name: 'completed_lock',
    activities: [
      { id: 'E500', description: 'Producción TJ-5 (avance parcial)', resource: 'LHD 01', type: 'Producción', bars: [
        { start: '2026-03-01T08:00:00', finish: '2026-03-03T00:00:00', quantityPercentage: '40', completed: true },
        { start: '2026-03-03T00:00:00', finish: '2026-03-06T08:00:00', quantityPercentage: '60' },
      ] },
      // Completion boundary mid-day: the civil day 2026-03-10 mixes completed
      // and pending fragments, so whole-activity moves must be rejected.
      { id: 'E510', description: 'Producción TJ-6 (corte a media jornada)', resource: 'LHD 02', type: 'Producción', bars: [
        { start: '2026-03-09T08:00:00', finish: '2026-03-10T12:00:00', quantityPercentage: '50', completed: true },
        { start: '2026-03-10T12:00:00', finish: '2026-03-11T20:00:00', quantityPercentage: '50' },
      ] },
      { id: 'F600', description: 'Relleno TJ-4 (completado)', resource: 'Planta relleno', type: 'Relleno', bars: [{ start: '2026-03-02T00:00:00', finish: '2026-03-04T00:00:00', completed: true }] },
      { id: 'G700', description: 'Perforación TJ-4', resource: 'Simba 01', type: 'Perforación', bars: [{ start: '2026-03-03T00:00:00', finish: '2026-03-04T00:00:00' }] },
      { id: 'H800', description: 'Carguío TJ-5', resource: 'Camión 01', type: 'Producción', bars: [{ start: '2026-03-06T08:00:00', finish: '2026-03-08T08:00:00' }] },
    ],
    links: [
      { key: 'L10', pred: 'G700', succ: 'F600', type: 'StartStart', lagHours: -24 },
      { key: 'L11', pred: 'E500', predBar: '2', succ: 'H800' },
    ],
  },
  {
    // J -> K is already violated in the native schedule (inherited conflict).
    // M -> N uses StartStart with a 24 h lag, P -> Q FinishFinish.
    name: 'precedence_conflict',
    activities: [
      { id: 'J100', description: 'Desarrollo acceso N2', resource: 'Jumbo 01', type: 'Desarrollo', bars: [{ start: '2026-03-02T00:00:00', finish: '2026-03-05T00:00:00' }] },
      { id: 'K200', description: 'Fortificación acceso N2', resource: 'Empernador 01', type: 'Fortificación', bars: [{ start: '2026-03-04T00:00:00', finish: '2026-03-06T00:00:00' }] },
      { id: 'M300', description: 'Perforación radial N2', resource: 'Simba 01', type: 'Perforación', bars: [{ start: '2026-03-03T00:00:00', finish: '2026-03-06T00:00:00' }] },
      { id: 'N400', description: 'Tronadura radial N2', resource: 'Cargador explosivos', type: 'Tronadura', bars: [{ start: '2026-03-04T00:00:00', finish: '2026-03-07T00:00:00' }] },
      { id: 'P500', description: 'Ventilación N2', resource: 'Ventilador 01', type: 'Servicios', bars: [{ start: '2026-03-02T00:00:00', finish: '2026-03-08T00:00:00' }] },
      { id: 'Q600', description: 'Instalación eléctrica N2', resource: 'Eléctricos', type: 'Servicios', bars: [{ start: '2026-03-05T00:00:00', finish: '2026-03-08T00:00:00' }] },
    ],
    links: [
      { key: 'L20', pred: 'J100', succ: 'K200' },
      { key: 'L21', pred: 'M300', succ: 'N400', type: 'StartStart', lagHours: 24 },
      { key: 'L22', pred: 'P500', succ: 'Q600', type: 'FinishFinish' },
    ],
  },
  {
    // S has two native bars (a VGS split) that share 2026-03-03, so that civil
    // day is one logical unit of two fragments; a gap day follows before the
    // second bar continues. T follows the second bar of S.
    name: 'native_splits',
    activities: [
      { id: 'S100', description: 'Desarrollo frente S (split)', resource: 'Jumbo 03', type: 'Desarrollo', bars: [
        { start: '2026-03-02T08:00:00', finish: '2026-03-03T12:00:00', quantityPercentage: '45' },
        { start: '2026-03-03T14:00:00', finish: '2026-03-04T20:00:00', quantityPercentage: '35' },
        { start: '2026-03-06T08:00:00', finish: '2026-03-06T20:00:00', quantityPercentage: '20' },
      ] },
      { id: 'T200', description: 'Fortificación frente S', resource: 'Empernador 02', type: 'Fortificación', bars: [{ start: '2026-03-07T00:00:00', finish: '2026-03-09T00:00:00' }] },
    ],
    links: [
      { key: 'L30', pred: 'S100', predBar: '3', succ: 'T200' },
    ],
  },
];

mkdirSync(OUT, { recursive: true });
for (const spec of SPECS) {
  const file = join(OUT, `${spec.name}.snapshot.json`);
  writeFileSync(file, JSON.stringify(buildSnapshot(spec), null, 2) + '\n', 'utf8');
  console.log(`wrote ${file}`);
}
