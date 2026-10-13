// Collects the JSON outputs of every test layer into reports/REPORT.md (rendered by GitHub).
import { existsSync, readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const R = new URL('../reports/', import.meta.url).pathname;
const read = (f) => (existsSync(join(R, f)) ? JSON.parse(readFileSync(join(R, f), 'utf8')) : null);
const lines = [`# Brainbox Translate — test report`, '', `Generated ${new Date().toISOString()}${process.env.GITHUB_SHA ? ` for commit ${process.env.GITHUB_SHA.slice(0, 8)}` : ''}.`, ''];

function vitest(name, j) {
  if (!j) { lines.push(`## ${name}`, '', '_not run_', ''); return; }
  lines.push(`## ${name}`, '', `**${j.numPassedTests} passed, ${j.numFailedTests} failed, ${j.numPendingTests} skipped** of ${j.numTotalTests}.`, '');
  for (const f of j.testResults) for (const t of f.assertionResults) if (t.status !== 'passed') lines.push(`- ${t.status === 'failed' ? '❌' : '⏭'} ${t.fullName}`);
  lines.push('');
}
vitest('Unit tests (Vitest)', read('unit-results.json'));
vitest('Engine tests (real Bergamot WASM + real models)', read('engine-results.json'));

const e2e = read('e2e-results.json');
lines.push('## End-to-end (Playwright, Chromium + unpacked extension)', '');
if (e2e) {
  const walk = (suite, out = []) => { for (const s of suite.specs ?? []) for (const t of s.tests) out.push({ title: s.title, file: s.file, project: t.projectName, status: t.results.at(-1)?.status ?? t.status, ann: t.annotations }); for (const c of suite.suites ?? []) walk(c, out); return out; };
  const tests = e2e.suites.flatMap((s) => walk(s));
  lines.push('| | Test | Project |', '|---|---|---|');
  for (const t of tests) lines.push(`| ${t.status === 'passed' ? '✅' : t.status === 'skipped' ? '⏭' : '❌'} | ${t.title}${t.ann?.length ? `<br><sub>${t.ann.map((a) => `${a.type}: ${a.description}`).join(' ')}</sub>` : ''} | ${t.project} |`);
} else lines.push('_not run_');
lines.push('');

const chrome = read('chrome-builtin.json');
lines.push('## Chrome built-in Translator API', '', chrome ? '```json\n' + JSON.stringify(chrome, null, 2) + '\n```' : '_not run_', '');
const perf = read('perf.json');
lines.push('## Performance (500 messages)', '', perf ? '```json\n' + JSON.stringify(perf, null, 2) + '\n```' : '_not run_', '');
const shots = existsSync(join(R, 'screenshots')) ? readdirSync(join(R, 'screenshots')).filter((f) => f.endsWith('.png')).sort() : [];
if (shots.length) { lines.push('## Screenshots', ''); for (const s of shots) lines.push(`### ${s.replace('.png', '')}`, '', `![${s}](screenshots/${s})`, ''); }
writeFileSync(join(R, 'REPORT.md'), lines.join('\n'));
console.log(join(R, 'REPORT.md'));
