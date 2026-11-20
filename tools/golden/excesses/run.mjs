// Golden outputs for timeline_logic.js previewExcesses (the capacity preview shown while dragging).
//
// previewExcesses is not exported, so its source text is cut out of reference/web/timeline_logic.js unchanged
// (from `function previewExcesses(` to the first line that is a lone `}`) and evaluated in a `vm` context. The
// baseline filter is the one inline in previewDailyTail (L873-877), restated here.
//
//   node tools/golden/excesses/run.mjs          regenerate expected.json
//   node tools/golden/excesses/run.mjs --check  compare with the committed file (exit 1 on drift)

import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(HERE, '..', '..', '..');
const LOGIC = join(ROOT, 'reference', 'web', 'timeline_logic.js');
const CASES = join(HERE, 'cases.json');
const OUT = join(HERE, 'expected.json');
const SEP = String.fromCharCode(0);

const source = readFileSync(LOGIC, 'utf8').replace(/\r\n/g, '\n');
const match = /^function previewExcesses\([\s\S]*?\n}\n/m.exec(source);
if (!match) throw new Error('previewExcesses not found in timeline_logic.js; update the loader');
const functionText = match[0];
const previewExcesses = vm.runInContext(`(${functionText.trim()})`, vm.createContext({}), { filename: 'timeline_logic.js#previewExcesses' });

const key = item => [item.kind, item.label, item.date].join(SEP);
const cases = JSON.parse(readFileSync(CASES, 'utf8')).cases.map(c => {
  const excesses = previewExcesses(c.view, c.preferences);
  const result = { name: c.name, excesses };
  if (c.baseline) {
    const known = new Set(previewExcesses(c.baseline, c.preferences).map(key));
    result.added = excesses.filter(item => !known.has(key(item)));
  }
  return JSON.parse(JSON.stringify(result));
});
const text = JSON.stringify({
  source: 'reference/web/timeline_logic.js previewExcesses',
  functionSha256: createHash('sha256').update(functionText, 'utf8').digest('hex'),
  cases,
}, null, 2) + '\n';

if (process.argv.includes('--check')) {
  const committed = readFileSync(OUT, 'utf8').replace(/\r\n/g, '\n');
  if (committed !== text) {
    console.error('excesses golden drift: run node tools/golden/excesses/run.mjs and review the diff');
    process.exit(1);
  }
  console.log(`excesses golden ok (${cases.length} cases)`);
} else {
  writeFileSync(OUT, text);
  console.log(`wrote ${OUT} (${cases.length} cases)`);
}
