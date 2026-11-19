// Records String.prototype.localeCompare results under Node (ICU) for the JsCompare tests.
//   node src/StpEngine.Tests/TestData/make_locale_compare.mjs
import { writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

let seed = 20261119;
const rand = () => { seed = (seed * 1103515245 + 12345) % 2147483648; return seed / 2147483648; };
const ascii = Array.from({ length: 95 }, (_, i) => String.fromCharCode(32 + i)).join('');
const id = ['A100', 'a100', 'A100:x1', 'A100-x1', 'A100_x1', 'A10', 'A1000', 'B200', 'b200', 'S100:x2:2:0:2026-03-03T14:00:00:2026-03-04T00:00:00',
  'S100:x1:1:1:2026-03-03T00:00:00:2026-03-03T12:00:00', '2026-03-02', '2026-03-02T08:00:00', '2026-03-02T08:00:00.5', 'Ab', 'aB', 'ab', 'AB', 'a b', 'a-b', 'a_b', ''];
const strings = [...id];
for (let n = 0; n < 300; n++) {
  const length = 1 + Math.floor(rand() * 6);
  let s = '';
  for (let i = 0; i < length; i++) s += ascii[Math.floor(rand() * ascii.length)];
  strings.push(s);
}
const pairs = [];
for (let n = 0; n < 3000; n++) {
  const a = strings[Math.floor(rand() * strings.length)], b = strings[Math.floor(rand() * strings.length)];
  pairs.push([a, b, Math.sign(a.localeCompare(b))]);
}
const sorted = strings.slice().sort((a, b) => a.localeCompare(b));
const out = join(dirname(fileURLToPath(import.meta.url)), 'locale_compare.json');
writeFileSync(out, JSON.stringify({ node: process.version, locale: Intl.DateTimeFormat().resolvedOptions().locale, sorted, pairs }) + '\n');
console.log(`wrote ${out}`);
