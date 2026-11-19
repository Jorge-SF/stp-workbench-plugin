// Golden-output harness for the STP V2 planner rules (no browser).
//
// Loads reference/web/planner_logic.js verbatim into a Node `vm` context,
// replays the operation scripts in fixtures/*.cases.json against the snapshots
// in fixtures/*.snapshot.json through the same controller the HTML page uses,
// and writes one JSON result per case plus the canonical apply-package text
// into expected/.
//
//   node tools/golden/run.mjs            regenerate expected/
//   node tools/golden/run.mjs --check    compare against expected/ (exit 1 on drift)
//   node tools/golden/run.mjs --out DIR  write to DIR instead of expected/

import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(HERE, '..', '..');
const LOGIC = join(ROOT, 'reference', 'web', 'planner_logic.js');
const FIXTURES = join(HERE, 'fixtures');

const args = process.argv.slice(2);
const check = args.includes('--check');
const outIndex = args.indexOf('--out');
const OUT = outIndex >= 0 ? resolve(args[outIndex + 1]) : join(HERE, 'expected');

// ---- load planner_logic.js as-is into an isolated realm --------------------
// The file is an ES module whose only module syntax is `export function` /
// `export async function`. We strip the `export` keyword (no other edits) and
// return the exported functions from the script, so no experimental VM-module
// flag is needed and the reference file stays byte-identical.
function loadPlannerLogic() {
  const source = readFileSync(LOGIC, 'utf8');
  if (/^\s*import\s/m.test(source)) throw new Error('planner_logic.js gained imports; update the loader');
  const names = [...source.matchAll(/^export\s+(?:async\s+)?function\s+([A-Za-z0-9_$]+)/gm)].map(m => m[1]);
  const script = source.replace(/^export\s+(?=(?:async\s+)?function\s)/gm, '')
    + `\n;({ ${names.join(', ')} });\n`;
  const context = vm.createContext({ TextEncoder });
  const api = vm.runInContext(script, context, { filename: 'planner_logic.js' });
  // Values must be created inside the realm so Array.isArray / prototypes match.
  const parse = vm.runInContext('(text => JSON.parse(text))', context);
  return { api, parse, names };
}

// ---- canonical JSON, identical to canonicalJsonValue/canonicalPackageText in
// short_term_planner_template_v2.html and to Python's
// json.dumps(sort_keys=True, separators=(",", ":"), ensure_ascii=False).
function canonicalJsonValue(value) {
  if (Array.isArray(value)) return value.map(canonicalJsonValue);
  if (value && typeof value === 'object') {
    return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonicalJsonValue(value[key])]));
  }
  return value;
}
const canonicalText = value => JSON.stringify(canonicalJsonValue(value));
const sha256 = text => createHash('sha256').update(Buffer.from(text, 'utf8')).digest('hex');
const plain = value => JSON.parse(JSON.stringify(value));

function memoryStorage(initial = {}) {
  const data = new Map(Object.entries(initial));
  return {
    getItem: key => (data.has(key) ? data.get(key) : null),
    setItem: (key, value) => { data.set(key, String(value)); },
    dump: () => Object.fromEntries([...data].sort(([a], [b]) => a.localeCompare(b))),
  };
}

function errorValue(error) {
  return { error: String(error?.message ?? error), code: error?.code ?? '' };
}

function fragmentRows(model) {
  return (model.fragments || []).slice()
    .sort((a, b) => (a.activityId === b.activityId ? a.id.localeCompare(b.id) : a.activityId.localeCompare(b.activityId)))
    .map(f => ({ id: f.id, start: f.start, finish: f.finish, provenance: f.provenance || '', lockedCompleted: f.lockedCompleted === true }));
}

function stateSummary(state) {
  return {
    horizon: state.horizon,
    displayHorizon: state.displayHorizon ?? null,
    hasPending: state.hasPending,
    hasLocalMovements: state.hasLocalMovements,
    canDownload: state.canDownload,
    status: state.status,
  };
}

function runStep(planner, logic, step, snapshot) {
  const { api } = logic;
  switch (step.op) {
    case 'moveActivity': return planner.moveActivity(step.activityId, step.days);
    case 'moveDailyTail': return planner.moveDailyTail(step.activityId, step.fragmentId, step.targetDate);
    case 'deferDailyTail': return planner.deferDailyTailToNextPeriod(step.activityId, step.fragmentId);
    case 'staggerDailyTail': return planner.staggerDailyTail(step.activityId, step.fragmentId, step.divisions, step.gapDays);
    case 'activityEligibility': return planner.activityEligibility(step.activityId);
    case 'fragmentEligibility': return planner.fragmentEligibility(step.fragmentId);
    case 'fragmentState': {
      const fragment = planner.state().model.fragments.find(f => f.id === step.fragmentId);
      return planner.fragmentState(fragment);
    }
    case 'editStart': return planner.editStart(step.value);
    case 'editFinish': return planner.editFinish(step.value);
    case 'editDuration': return planner.editDuration(step.value);
    case 'savePreference': return planner.savePreference();
    case 'reset': return planner.reset();
    case 'assessPrecedenceConflicts': return api.assessPrecedenceConflicts(planner.state().model, step.policy);
    // Pure module functions on the current model; the controller state is not changed.
    case 'moveFragment': return { fragments: fragmentRows(api.moveFragment(planner.state().model, step.fragmentId, step.newStart)) };
    case 'buildScenario': return api.buildScenario(planner.state().model, {
      projectFingerprint: snapshot.projectFingerprint,
      precedenceFingerprint: snapshot.precedenceFingerprint,
    }, planner.state().horizon);
    default: throw new Error(`unknown op ${step.op}`);
  }
}

function runCase(logic, snapshotText, snapshot, testCase) {
  const { api, parse } = logic;
  const seeded = Object.fromEntries(Object.entries(testCase.storage || {})
    .map(([key, value]) => [key.replace('{projectKey}', snapshot.projectKey), typeof value === 'string' ? value : JSON.stringify(value)]));
  const storage = memoryStorage(seeded);
  const result = { case: testCase.name, description: testCase.description || '', steps: [] };
  let planner;
  try {
    const model = api.validatePlannerModel(api.buildPlannerModel(parse(snapshotText)));
    const initialHorizon = testCase.initialHorizon ? parse(JSON.stringify(testCase.initialHorizon)) : null;
    planner = api.createCivilHorizonController(() => model, snapshot.projectKey, storage, initialHorizon);
  } catch (error) {
    result.setupError = errorValue(error);
    return { result, canonical: null };
  }
  result.initial = stateSummary(planner.state());
  for (const step of testCase.steps || []) {
    let outcome;
    try { outcome = plain(runStep(planner, logic, step, snapshot)); } catch (error) { outcome = errorValue(error); }
    result.steps.push({ step, outcome });
  }
  const state = planner.state();
  result.final = stateSummary(state);
  result.fragments = fragmentRows(state.model);
  result.storage = storage.dump();
  let canonical = null;
  try {
    const pkg = plain(api.buildVgsApplyPackage(state.model, {
      projectFingerprint: snapshot.projectFingerprint,
      precedenceFingerprint: snapshot.precedenceFingerprint,
    }, state.horizon));
    canonical = canonicalText(pkg);
    result.applyPackage = { sha256: sha256(canonical), package: pkg };
  } catch (error) {
    result.applyPackage = errorValue(error);
  }
  return { result, canonical };
}

function generate() {
  const logic = loadPlannerLogic();
  const files = new Map();
  const index = [];
  const caseFiles = readdirSync(FIXTURES).filter(name => name.endsWith('.cases.json')).sort();
  for (const caseFile of caseFiles) {
    const fixture = caseFile.replace(/\.cases\.json$/, '');
    const snapshotText = readFileSync(join(FIXTURES, `${fixture}.snapshot.json`), 'utf8');
    const snapshot = JSON.parse(snapshotText);
    if (snapshot.schema !== 'vgs-stp-snapshot/2') throw new Error(`${fixture}: unexpected schema ${snapshot.schema}`);
    const { cases } = JSON.parse(readFileSync(join(FIXTURES, caseFile), 'utf8'));
    for (const testCase of cases) {
      const { result, canonical } = runCase(logic, snapshotText, snapshot, testCase);
      const base = `${fixture}.${testCase.name}`;
      files.set(`${base}.json`, JSON.stringify(result, null, 2) + '\n');
      if (canonical !== null) files.set(`${base}.apply.canonical.json`, canonical);
      index.push({
        fixture, case: testCase.name,
        steps: result.steps.map(({ outcome }) => {
          if (outcome && typeof outcome === 'object' && 'ok' in outcome) return outcome.ok ? 'ok' : `fail:${outcome.code}`;
          if (outcome && typeof outcome === 'object' && 'error' in outcome) return 'error';
          return 'value';
        }),
        applySha256: result.applyPackage?.sha256 ?? null,
        applyError: result.applyPackage?.error ?? null,
      });
    }
  }
  const logicSha = sha256(readFileSync(LOGIC, 'utf8'));
  files.set('index.json', JSON.stringify({ plannerLogicSha256: logicSha, exports: logic.names, cases: index }, null, 2) + '\n');
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
  console.log(drift ? `${drift} file(s) differ from ${OUT}` : `OK: ${files.size} golden files match ${OUT}`);
  process.exit(drift ? 1 : 0);
} else {
  if (existsSync(OUT)) rmSync(OUT, { recursive: true });
  mkdirSync(OUT, { recursive: true });
  for (const [name, text] of files) writeFileSync(join(OUT, name), text, 'utf8');
  console.log(`wrote ${files.size} files to ${OUT}`);
}
