// Golden-output harness for the local actual-progress rules ("Avance real") of the browser planner.
//
// Loads reference/web/v1_compat_logic.js verbatim into a Node `vm` context and replays the steps of
// fixtures/actual_progress.steps.json: the actualProgress* / localActualProgress* module functions plus the
// template glue that combines them (short_term_planner_template_v2.html applyVisibleActualProgressSlotCascade,
// applyVisibleActualProgressCascade, the value input handler and the context-menu rules), copied here verbatim
// except that the template's `currentView` row lookup is a `row` argument.
//
//   node tools/golden/actual_progress.mjs            regenerate expected_actual_progress/
//   node tools/golden/actual_progress.mjs --check    compare with the committed files (exit 1 on drift)
//
// State is written as ordered entry lists (JS property order) so the C# port is checked for order too.

import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(HERE, '..', '..');
const LOGIC = join(ROOT, 'reference', 'web', 'v1_compat_logic.js');
const CASES = join(HERE, 'fixtures', 'actual_progress.steps.json');
const OUT = join(HERE, 'expected_actual_progress');
const check = process.argv.includes('--check');

function loadCompatLogic() {
  const source = readFileSync(LOGIC, 'utf8');
  if (/^\s*import\s/m.test(source)) throw new Error('v1_compat_logic.js gained imports; update the loader');
  const names = [...source.matchAll(/^export\s+function\s+([A-Za-z0-9_$]+)/gm)].map(m => m[1]);
  const script = source.replace(/^export\s+(?=function\s)/gm, '') + `\n;({ ${names.join(', ')} });\n`;
  const context = vm.createContext({});
  const api = vm.runInContext(script, context, { filename: 'v1_compat_logic.js' });
  const parse = vm.runInContext('(text => JSON.parse(text))', context);
  return { api, parse, names };
}

const { api, parse, names } = loadCompatLogic();
// Fixture numbers: JSON cannot hold NaN/Infinity, so the strings "NaN", "Infinity", "-Infinity" stand for them.
const num = value => (value === 'NaN' ? Number.NaN : value === 'Infinity' ? Infinity : value === '-Infinity' ? -Infinity : value);
const inRealm = value => (value === undefined ? undefined : parse(JSON.stringify(value)));

function stateOut(state) {
  const s = api.standaloneLocalState(state);
  return {
    byDay: Object.entries(s.actualProgressByDay),
    byFragment: Object.entries(s.actualProgressByFragment),
    slots: Object.entries(s.actualProgressSlotsByDay).map(([key, value]) => [key, value]),
    hidden: Object.entries(s.hiddenActualProgressSlotsByDay).map(([key, value]) => [key, value]),
  };
}

function stateIn(raw) {
  const entries = list => Object.fromEntries((list || []).map(([key, value]) => [key, num(value)]));
  return parse(JSON.stringify({
    actualProgressByDay: entries(raw?.byDay),
    actualProgressByFragment: entries(raw?.byFragment),
    actualProgressSlotsByDay: entries(raw?.slots),
    hiddenActualProgressSlotsByDay: entries(raw?.hidden),
  }));
}

// A fixture row: { activityId, type, resources, days: [{ date, segments: [{ status, amount, resource }] }] }.
// Segments carry the amount both as scheduledAdvance (buildActualProgressByType) and scheduledAmount
// (buildActivityPeriodTotals), like the template's compatibility view.
function rowIn(row) {
  return inRealm({
    activityId: row.activityId,
    activityType: row.type,
    resources: row.resources,
    days: (row.days || []).map(day => ({
      date: day.date,
      segments: (day.segments || []).map(segment => ({
        status: segment.status,
        scheduledAdvance: segment.amount,
        scheduledAmount: segment.amount,
        resource: segment.resource,
      })),
    })),
  });
}

function viewIn(view) {
  return { days: inRealm((view.dates || []).map(date => ({ date }))), rows: (view.rows || []).map(rowIn) };
}

function byTypeOut(result) {
  const totals = value => Object.entries(value);
  return {
    dates: [...result.dates],
    byType: Object.values(result.byType).map(row => ({
      type: row.type,
      days: Object.entries(row.days).map(([date, day]) => [date, { scheduled: day.scheduled, actual: day.actual }]),
      plannedByResource: totals(row.plannedByResource),
      actualByResource: totals(row.actualByResource),
      plannedByResourceByDate: Object.entries(row.plannedByResourceByDate).map(([date, value]) => [date, totals(value)]),
      actualByResourceByDate: Object.entries(row.actualByResourceByDate).map(([date, value]) => [date, totals(value)]),
    })),
  };
}

// ---- template glue (short_term_planner_template_v2.html ~3724-3795, 3981-4016, 4532-4545) ----
function actualProgressDayIsScheduled(row, date) {
  const day = row?.days?.find(candidate => candidate.date === date);
  return Boolean(day?.segments?.length);
}
function visibleActualProgressDates(row) {
  return Array.isArray(row?.days) ? row.days.map(day => day.date).filter(Boolean) : [];
}
function visibleActualProgressSlotDates(localState, row) {
  const activityId = row.activityId;
  return Array.isArray(row?.days) ? row.days
    .filter(day => day.date && !api.hasHiddenLocalActualProgressSlot(localState, activityId, day.date)
      && (day.segments.length > 0 || api.hasLocalActualProgressSlot(localState, activityId, day.date)))
    .map(day => day.date) : [];
}
function applyVisibleActualProgressSlotCascade(localState, row, originDate, targetDate) {
  const activityId = row.activityId;
  const moves = api.visibleLocalActualProgressSlotCascadeMoves(
    localState, activityId, originDate, targetDate,
    visibleActualProgressDates(row), visibleActualProgressSlotDates(localState, row),
  );
  if (!moves.length) return { applied: false, state: localState };
  const visibleDates = visibleActualProgressDates(row);
  const visibleSlotDates = visibleActualProgressSlotDates(localState, row);
  localState = api.moveVisibleLocalActualProgressSlotCascade(
    localState, activityId, originDate, targetDate, visibleDates, visibleSlotDates,
  );
  for (const move of moves) {
    if (actualProgressDayIsScheduled(row, move.originDate)) {
      localState = api.setHiddenLocalActualProgressSlot(localState, activityId, move.originDate, true);
    } else {
      localState = api.setLocalActualProgressSlot(localState, activityId, move.originDate, false);
    }
  }
  for (const move of moves) {
    localState = api.restoreLocalActualProgressSlot(
      localState, activityId, move.targetDate, actualProgressDayIsScheduled(row, move.targetDate),
    );
  }
  return { applied: true, state: localState };
}
function applyVisibleActualProgressCascade(localState, row, originDate, targetDate) {
  const activityId = row.activityId;
  const visibleDates = visibleActualProgressDates(row);
  const moves = api.visibleLocalActualProgressCascadeMoves(localState, activityId, originDate, targetDate, visibleDates);
  if (!moves.length) return { applied: false, state: localState };
  localState = api.moveVisibleLocalActualProgressCascade(localState, activityId, originDate, targetDate, visibleDates);
  for (const move of moves) {
    if (actualProgressDayIsScheduled(row, move.originDate)) {
      localState = api.setHiddenLocalActualProgressSlot(localState, activityId, move.originDate, true);
    } else {
      localState = api.setLocalActualProgressSlot(localState, activityId, move.originDate, false);
    }
  }
  for (const move of moves) {
    localState = api.restoreLocalActualProgressSlot(
      localState, activityId, move.targetDate, actualProgressDayIsScheduled(row, move.targetDate),
    );
  }
  return { applied: true, state: localState };
}
// renderTimelineRow: a day shows the value box when it is not hidden and is scheduled or has an explicit slot.
function showsControl(localState, row, date) {
  const day = row?.days?.find(candidate => candidate.date === date);
  if (!day) return false;
  const isScheduledDay = day.segments.length > 0;
  return !api.hasHiddenLocalActualProgressSlot(localState, row.activityId, date)
    && (isScheduledDay || api.hasLocalActualProgressSlot(localState, row.activityId, date));
}
// handleTimelineContextMenu: "Agregar avance real" on a track day without a box.
function canAddSlot(localState, row, date) {
  const day = row?.days?.find(candidate => candidate.date === date);
  const scheduled = day?.segments.length > 0;
  const hidden = api.hasHiddenLocalActualProgressSlot(localState, row.activityId, date);
  return !(!date || !day || api.hasLocalActualProgressSlot(localState, row.activityId, date) || (scheduled && !hidden));
}
// The value input's change handler: blank deletes the day's value, anything else goes through setLocalActualProgress.
function editValue(localState, activityId, date, value) {
  if (value === null) {
    localState = api.standaloneLocalState(localState);
    delete localState.actualProgressByDay[JSON.stringify([activityId, date])];
    return localState;
  }
  return api.setLocalActualProgress(localState, activityId, date, num(value));
}

function runStep(state, step) {
  const row = step.row ? rowIn(step.row) : null;
  const dates = step.dates ? inRealm(step.dates) : undefined;
  switch (step.op) {
    // queries
    case 'value': { const v = api.localActualProgress(state, step.activityId, step.date); return { state, outcome: v === undefined ? null : v }; }
    case 'hasSlot': return { state, outcome: api.hasLocalActualProgressSlot(state, step.activityId, step.date) };
    case 'hasHidden': return { state, outcome: api.hasHiddenLocalActualProgressSlot(state, step.activityId, step.date) };
    case 'hasVisibleSlotInDays': return { state, outcome: api.hasVisibleLocalActualProgressSlotInDays(state, step.activityId, dates) };
    case 'visibleSlotActivityIds': return { state, outcome: api.visibleLocalActualProgressActivityIdsInDays(state, dates) };
    case 'cascadeMoves': return { state, outcome: api.visibleLocalActualProgressCascadeMoves(state, step.activityId, step.originDate, step.targetDate, dates) };
    case 'slotCascadeMoves': return { state, outcome: api.visibleLocalActualProgressSlotCascadeMoves(state, step.activityId, step.originDate, step.targetDate, dates, inRealm(step.slotDates)) };
    case 'byType': return { state, outcome: byTypeOut(api.buildActualProgressByType(viewIn(step.view), state)) };
    case 'activityPeriodTotals': return { state, outcome: api.buildActivityPeriodTotals(row, state, step.show === true) };
    case 'typePeriodTotals': {
      const result = api.buildActualProgressByType(viewIn(step.view), state);
      return { state, outcome: Object.values(result.byType).map(typeRow => [typeRow.type, api.buildActualProgressPeriodTotals(typeRow, dates)]) };
    }
    case 'hasAny': return { state, outcome: Object.keys(state.actualProgressByFragment || {}).length > 0
      || Object.keys(state.actualProgressByDay || {}).length > 0 || Object.keys(state.actualProgressSlotsByDay || {}).length > 0 };
    case 'visibleSlotDates': return { state, outcome: visibleActualProgressSlotDates(state, row) };
    case 'showsControl': return { state, outcome: showsControl(state, row, step.date) };
    case 'canAddSlot': return { state, outcome: canAddSlot(state, row, step.date) };
    // state changes
    case 'setValue': return { state: api.setLocalActualProgress(state, step.activityId, step.date, num(step.value)), outcome: null };
    case 'setFragmentValue': return { state: api.setLocalActualProgress(state, step.fragmentId, num(step.value)), outcome: null };
    case 'editValue': return { state: editValue(state, step.activityId, step.date, step.value), outcome: null };
    case 'setSlot': return { state: api.setLocalActualProgressSlot(state, step.activityId, step.date, step.present), outcome: null };
    case 'setHidden': return { state: api.setHiddenLocalActualProgressSlot(state, step.activityId, step.date, step.hidden), outcome: null };
    case 'clearSlot': return { state: api.clearLocalActualProgressSlot(state, step.activityId, step.date, step.scheduled), outcome: null };
    case 'restoreSlot': return { state: api.restoreLocalActualProgressSlot(state, step.activityId, step.date, step.scheduled), outcome: null };
    case 'move': return { state: api.moveLocalActualProgress(state, step.activityId, step.originDate, step.targetDate), outcome: null };
    case 'moveCascade': return { state: api.moveVisibleLocalActualProgressCascade(state, step.activityId, step.originDate, step.targetDate, dates), outcome: null };
    case 'moveSlotCascade': return { state: api.moveVisibleLocalActualProgressSlotCascade(state, step.activityId, step.originDate, step.targetDate, dates, inRealm(step.slotDates)), outcome: null };
    case 'migrate': return { state: api.migrateLocalActualProgress(state, inRealm(step.segments)), outcome: null };
    case 'applySlotCascade': { const r = applyVisibleActualProgressSlotCascade(state, row, step.originDate, step.targetDate); return { state: r.state, outcome: r.applied }; }
    case 'applyCascade': { const r = applyVisibleActualProgressCascade(state, row, step.originDate, step.targetDate); return { state: r.state, outcome: r.applied }; }
    default: throw new Error(`unknown op ${step.op}`);
  }
}

function generate() {
  const files = new Map();
  const { cases } = JSON.parse(readFileSync(CASES, 'utf8'));
  for (const testCase of cases) {
    let state = stateIn(testCase.state);
    const result = { case: testCase.name, description: testCase.description || '', initial: stateOut(state), steps: [] };
    for (const step of testCase.steps) {
      const next = runStep(state, step);
      state = next.state;
      result.steps.push({ op: step.op, outcome: JSON.parse(JSON.stringify(next.outcome ?? null)), state: stateOut(state) });
    }
    files.set(`${testCase.name}.json`, JSON.stringify(result, null, 2) + '\n');
  }
  const sha = createHash('sha256').update(readFileSync(LOGIC)).digest('hex');
  files.set('index.json', JSON.stringify({ compatLogicSha256: sha, exports: names, cases: cases.map(c => c.name) }, null, 2) + '\n');
  return files;
}

const files = generate();
if (check) {
  let drift = 0;
  const expected = existsSync(OUT) ? readdirSync(OUT).sort() : [];
  for (const name of new Set([...expected, ...files.keys()])) {
    const want = expected.includes(name) ? readFileSync(join(OUT, name), 'utf8') : null;
    const got = files.get(name) ?? null;
    if (want !== got) { drift += 1; console.error(`DRIFT ${name}${want === null ? ' (new)' : got === null ? ' (missing)' : ''}`); }
  }
  console.log(drift ? `${drift} file(s) differ from ${OUT}` : `OK: ${files.size} actual-progress golden files match ${OUT}`);
  process.exit(drift ? 1 : 0);
} else {
  if (existsSync(OUT)) rmSync(OUT, { recursive: true });
  mkdirSync(OUT, { recursive: true });
  for (const [name, text] of files) writeFileSync(join(OUT, name), text, 'utf8');
  console.log(`wrote ${files.size} files to ${OUT}`);
}
