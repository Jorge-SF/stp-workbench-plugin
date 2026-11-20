// Golden-output harness for the manual resources of the V1 compatibility layer (no browser).
//
// Loads reference/web/v1_compat_logic.js verbatim into a Node `vm` context and replays cases/*.cases.json through
// normalizeManualResources / normalizeCompatibilityPreferences / resourceOptions plus line-for-line copies of the
// template's addManualResource, renameManualResource, removeManualResource and set*ResourceOverride
// (reference/web/short_term_planner_template_v2.html L4195-L4290). One JSON result per case goes to expected/.
//
//   node tools/golden/compat/run.mjs            regenerate expected/
//   node tools/golden/compat/run.mjs --check    compare against expected/ (exit 1 on drift)

import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(HERE, '..', '..', '..');
const LOGIC = join(ROOT, 'reference', 'web', 'v1_compat_logic.js');
const CASES = join(HERE, 'cases');
const OUT = join(HERE, 'expected');
const check = process.argv.includes('--check');

function loadCompatLogic() {
  const source = readFileSync(LOGIC, 'utf8');
  if (/^\s*import\s/m.test(source)) throw new Error('v1_compat_logic.js gained imports; update the loader');
  const names = [...source.matchAll(/^(?:export\s+)?function\s+([A-Za-z0-9_$]+)/gm)].map(m => m[1]);
  const script = source.replace(/^export\s+(?=function\s)/gm, '') + `\n;({ ${names.join(', ')} });\n`;
  const context = vm.createContext({});
  const api = vm.runInContext(script, context, { filename: 'v1_compat_logic.js' });
  const parse = vm.runInContext('(text => JSON.parse(text))', context);
  return { api, parse };
}

const plain = value => JSON.parse(JSON.stringify(value));

function runCase(logic, testCase) {
  const { api, parse } = logic;
  const snapshot = parse(JSON.stringify({
    activities: testCase.vgsResources.map((resource, index) => ({ id: `A${index + 1}`, grid: { Resource: resource } })),
  }));
  const catalog = api.deriveCompatibilityCatalog(snapshot);
  let preferences = api.normalizeCompatibilityPreferences({}, catalog);
  let notice = '';
  const update = patch => { preferences = api.normalizeCompatibilityPreferences({ ...preferences, ...patch }, catalog); };

  // ---- copies of the template functions (busy() is always false here) --------------------------------------
  function addManualResource() {
    const existing = new Set(preferences.manualResources.map(item => item.id));
    let number = 1;
    while (existing.has(`manual:${number}`)) number += 1;
    let nameNumber = number;
    while (true) {
      const manualResources = [...preferences.manualResources, { id: `manual:${number}`, name: `Equipo manual ${nameNumber}` }];
      const normalized = api.normalizeCompatibilityPreferences({ ...preferences, manualResources }, catalog);
      if (normalized.manualResources.some(item => item.id === `manual:${number}`)) { update({ manualResources }); return; }
      nameNumber += 1;
    }
  }
  function renameManualResource(id, name) {
    if (!id) return;
    const manualResources = preferences.manualResources.map(resource => (resource.id === id ? { ...resource, name } : resource));
    const normalized = api.normalizeCompatibilityPreferences({ ...preferences, manualResources }, catalog);
    const accepted = normalized.manualResources.find(resource => resource.id === id);
    if (!accepted || accepted.name !== String(name || '').trim()) {
      notice = 'El nombre del recurso manual debe ser único y no estar vacío.';
      return;
    }
    update({ manualResources });
  }
  function removeManualResource(id) {
    if (!id) return;
    const removed = preferences.manualResources.find(resource => resource.id === id);
    if (!removed) return;
    const withoutResource = overrides => Object.fromEntries(Object.entries(overrides || {}).filter(([, resourceId]) => resourceId !== id));
    update({
      manualResources: preferences.manualResources.filter(resource => resource.id !== id),
      activityResourceOverrides: withoutResource(preferences.activityResourceOverrides),
      fragmentResourceOverrides: withoutResource(preferences.fragmentResourceOverrides),
    });
  }
  function setActivityResourceOverride(activityId, resourceId) {
    const activityResourceOverrides = { ...preferences.activityResourceOverrides };
    if (resourceId) activityResourceOverrides[activityId] = resourceId; else delete activityResourceOverrides[activityId];
    update({ activityResourceOverrides });
  }
  function setFragmentResourceOverride(logicalUnitId, resourceId) {
    const fragmentResourceOverrides = { ...preferences.fragmentResourceOverrides };
    if (resourceId && resourceId !== '__activity_default__') fragmentResourceOverrides[logicalUnitId] = resourceId;
    else delete fragmentResourceOverrides[logicalUnitId];
    update({ fragmentResourceOverrides });
  }

  const steps = [];
  for (const step of testCase.steps) {
    notice = '';
    switch (step.op) {
      case 'load': preferences = api.normalizeCompatibilityPreferences(parse(JSON.stringify(step.preferences)), catalog); break;
      case 'add': addManualResource(); break;
      case 'rename': renameManualResource(step.id, step.name); break;
      case 'remove': removeManualResource(step.id); break;
      case 'assignActivity': setActivityResourceOverride(step.activityId, step.resourceId); break;
      case 'assignFragment': setFragmentResourceOverride(step.logicalUnitId, step.resourceId); break;
      default: throw new Error(`unknown op ${step.op}`);
    }
    steps.push({
      step,
      notice,
      manualResources: plain(preferences.manualResources),
      options: plain(api.resourceOptions(catalog, preferences)).map(({ id, name, source }) => ({ id, name, source })),
      activityResourceOverrides: plain(preferences.activityResourceOverrides),
      fragmentResourceOverrides: plain(preferences.fragmentResourceOverrides),
    });
  }
  return { case: testCase.name, description: testCase.description || '', vgsResources: testCase.vgsResources, steps };
}

function generate() {
  const logic = loadCompatLogic();
  const files = new Map();
  for (const caseFile of readdirSync(CASES).filter(name => name.endsWith('.cases.json')).sort()) {
    const fixture = caseFile.replace(/\.cases\.json$/, '');
    const { cases } = JSON.parse(readFileSync(join(CASES, caseFile), 'utf8'));
    for (const testCase of cases) files.set(`${fixture}.${testCase.name}.json`, JSON.stringify(runCase(logic, testCase), null, 2) + '\n');
  }
  const sha = createHash('sha256').update(readFileSync(LOGIC)).digest('hex');
  files.set('index.json', JSON.stringify({ compatLogicSha256: sha, cases: [...files.keys()].sort() }, null, 2) + '\n');
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
  console.log(drift ? `${drift} file(s) differ from ${OUT}` : `OK: ${files.size} compat golden files match ${OUT}`);
  process.exit(drift ? 1 : 0);
} else {
  if (existsSync(OUT)) rmSync(OUT, { recursive: true });
  mkdirSync(OUT, { recursive: true });
  for (const [name, text] of files) writeFileSync(join(OUT, name), text, 'utf8');
  console.log(`wrote ${files.size} files to ${OUT}`);
}
