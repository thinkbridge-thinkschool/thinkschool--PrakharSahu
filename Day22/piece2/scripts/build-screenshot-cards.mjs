// Builds the HTML cards that Screenshots/*.jpg are captured from.
//
// Every line of output on a card is read out of docs/*.txt at build time. Nothing is retyped, so
// a card cannot drift away from the run it claims to show.
//
//   node scripts/build-screenshot-cards.mjs
//   npx http-server .shots -p 8099        (file:// is blocked by the capture tool)

import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const out = join(root, '.shots');
mkdirSync(out, { recursive: true });

const NL = String.fromCharCode(10);
const read = (p) => readFileSync(join(root, p), 'utf8');

const smoke = read('docs/smoke-output.txt');
const graph = read('docs/reference-graph.txt');
const guardrail = read('docs/architecture-guardrail-proof.txt');
const tests = read('docs/test-results.txt');

/** Pulls one "==== N. Title ====" block out of the smoke log. */
function section(number) {
  const blocks = smoke.split(/={60,}/);
  for (let i = 0; i < blocks.length - 1; i++) {
    if (blocks[i + 1]?.trim().startsWith(`${number}.`)) {
      return `${blocks[i + 1].trim()}${NL}${blocks[i + 2] ?? ''}`.replace(/\n{3,}/g, NL + NL).trimEnd();
    }
  }
  throw new Error(`smoke section ${number} not found`);
}

const escape = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

function highlight(text) {
  return escape(text)
    .replace(/\[PASS\]/g, '<span class="pass">[PASS]</span>')
    .replace(/\[FAIL\]/g, '<span class="fail">[FAIL]</span>')
    .replace(/\bPassed!/g, '<span class="pass">Passed!</span>')
    .replace(/\bFailed!/g, '<span class="fail">Failed!</span>')
    .replace(/(\d+) passed/g, '<span class="pass">$1 passed</span>')
    .replace(/(\b0 failed)/g, '<span class="pass">$1</span>')
    .replace(/\bContracts\b/g, '<span class="door">Contracts</span>')
    .replace(/\bSharedKernel\b/g, '<span class="kernel">SharedKernel</span>');
}

/** Splits a long block into two side-by-side panes; the capture viewport cannot grow. */
function columns(body, threshold = 30) {
  const lines = body.split(NL);
  if (lines.length <= threshold) {
    return `<pre>${highlight(body)}</pre>`;
  }

  let cut = Math.ceil(lines.length / 2);
  for (let i = cut; i < Math.min(lines.length - 4, cut + 10); i++) {
    if (lines[i].trim() === '') { cut = i + 1; break; }
  }

  return `<pre>${highlight(lines.slice(0, cut).join(NL).trimEnd())}</pre>`
       + `<pre>${highlight(lines.slice(cut).join(NL).trimStart())}</pre>`;
}

function card({ file, eyebrow, title, lead, body, footnote, split }) {
  const html = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>${escape(title)}</title>
<style>
  :root {
    --ink: #1c1a22; --muted: #5d5870; --accent: #6b4ea8;
    --accent-soft: #f2eefb; --rule: #e2dcf0;
    --term-bg: #17151f; --term-ink: #d8d3e4;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 26px 34px; background: #fff; color: var(--ink);
    font-family: "Avenir Next LT Pro", "Avenir Next", Mulish, "Segoe UI", system-ui, sans-serif;
    font-size: 13px; line-height: 1.45; width: 1470px;
  }
  .eyebrow {
    font-size: 11px; letter-spacing: .13em; text-transform: uppercase;
    color: var(--accent); font-weight: 600; margin-bottom: 10px;
  }
  h1 { font-size: 26px; line-height: 1.18; margin: 0 0 9px; font-weight: 700; letter-spacing: -.01em; }
  .lead { font-size: 14px; color: var(--muted); margin: 0 0 16px; max-width: 165ch; }
  .term { display: flex; gap: 14px; }
  pre {
    background: var(--term-bg); color: var(--term-ink); padding: 16px 18px; border-radius: 9px;
    font-family: "Cascadia Code", "JetBrains Mono", Consolas, ui-monospace, monospace;
    font-size: 11px; line-height: 1.46; margin: 0; flex: 1 1 0; min-width: 0;
    overflow-x: auto; white-space: pre;
  }
  .pass   { color: #7ee0a0; font-weight: 600; }
  .fail   { color: #ff8b8b; font-weight: 600; }
  .door   { color: #ffd479; font-weight: 600; }
  .kernel { color: #9fc7ff; }
  .note {
    margin-top: 14px; padding: 11px 16px; background: var(--accent-soft);
    border-radius: 8px; color: var(--ink); font-size: 13px;
  }
  .note strong { color: var(--accent); }
  footer {
    margin-top: 16px; padding-top: 10px; border-top: 1px solid var(--rule);
    color: var(--muted); font-size: 11px; display: flex; justify-content: space-between;
  }
</style>
</head>
<body>
  <div class="eyebrow">${escape(eyebrow)}</div>
  <h1>${escape(title)}</h1>
  <p class="lead">${lead}</p>
  <div class="term">${columns(body, split ? 0 : 30)}</div>
  ${footnote ? `<div class="note">${footnote}</div>` : ''}
  <footer>
    <span>Day 22 &middot; piece2 &middot; Dispatch &mdash; capstone kickoff</span>
    <span>generated from docs/ by scripts/build-screenshot-cards.mjs</span>
  </footer>
</body>
</html>`;
  writeFileSync(join(out, file), html, 'utf8');
  return file;
}

const cards = [
  card({
    file: '01-reference-graph.html',
    eyebrow: 'The architecture',
    title: 'Sixteen projects, three cross-module edges, all through Contracts',
    lead: 'Read straight out of the .csproj files — the same source the architecture tests parse. '
        + 'Every module has the same four layers, SharedKernel depends on nothing, and no module '
        + 'reaches past another module’s Contracts.',
    body: graph.trim(),
    split: true,
    footnote: 'Three edges cross a module boundary, and every one of them lands on '
            + '<strong>*.Contracts</strong>. A module that can reach another module’s Domain '
            + 'has no boundary at all &mdash; it can depend on internal types, so the other module '
            + 'can no longer change them, so the two are one module wearing two folder names.'
  }),
  card({
    file: '02-guardrail-proof.html',
    eyebrow: 'Enforcement',
    title: 'The boundary is a failing build, not a paragraph in a README',
    lead: 'A guard rail that has never been tripped is not a guard rail. This adds the single most '
        + 'damaging reference possible — one module’s Domain reaching into another’s — and '
        + 'shows three separate rules refusing it.',
    body: guardrail.trim(),
    split: true,
    footnote: 'Nobody adds a forbidden reference on purpose. They add it at 5pm because the type '
            + 'they needed happened to be over there, and by the time anyone notices there are '
            + 'forty of them and <strong>the boundary is gone</strong>.'
  }),
  card({
    file: '03-state-machine-refuses.html',
    eyebrow: 'The aggregate',
    title: 'The state machine refuses out-of-order transitions',
    lead: 'Over HTTP, against the running host. Starting or scheduling an untriaged order is '
        + 'rejected by the aggregate itself, not by a check in a controller.',
    body: `${section(1)}${NL}${NL}${section(2)}${NL}${NL}${section(3)}`,
    footnote: '<strong>409, not 400.</strong> The request was well-formed; the resource was simply '
            + 'not in a state that allows it. A client that retries a 400 is confused; a client '
            + 'that refreshes and retries a 409 is behaving correctly.'
  }),
  card({
    file: '04-scheduling-saga.html',
    eyebrow: 'Async flow 1 · saga',
    title: 'A clashing booking compensates itself back to triage',
    lead: 'WorkManagement commits “Scheduled” and publishes intent. Scheduling — the only module '
        + 'that can see a calendar — refuses. WorkManagement walks the order back. No distributed '
        + 'transaction anywhere.',
    body: `${section(4)}${NL}${NL}${section(5)}`,
    footnote: 'Two modules cannot share a transaction, so the price of the boundary is a '
            + '<strong>compensating action</strong>. Returning to triage is a first-class domain '
            + 'operation rather than a quiet field reset, because “we told you we were coming and '
            + 'now we are not” is a real business event.'
  }),
  card({
    file: '05-release-and-rebook.html',
    eyebrow: 'Async flow 2 · decoupling',
    title: 'Cancelling frees the slot; nothing unfinished is ever invoiced',
    lead: 'Billing only ever hears WorkOrderCompletedV1, so there is no code path where an '
        + 'abandoned job produces a bill. Scheduling hears the release and hands the window back.',
    body: `${section(6)}${NL}${NL}${section(7)}`,
    footnote: 'A field engineer taps “done” on a phone with two bars of signal. If completing the '
            + 'job required Billing to price and store an invoice in the same request, '
            + '<strong>an accounting problem would stop engineers finishing work</strong>.'
  }),
  card({
    file: '06-tests-green.html',
    eyebrow: 'Verification',
    title: 'Fifty-eight tests, and one of them found a real bug',
    lead: 'Twelve architecture rules, thirty-five aggregate invariants, eleven cross-module flows, '
        + 'plus eight assertions over HTTP against the running host.',
    body: `${tests.trim()}${NL}${NL}${smoke.split(/={60,}/).pop().trim()}`,
    footnote: 'A_double_booked_technician_sends_the_order_back_to_triage failed with “Collection '
            + 'was modified”. The in-process bus makes publishing <strong>re-entrant</strong>: the '
            + 'compensating handler mutated the same aggregate while the publish loop was still '
            + 'iterating its events. Fixed by snapshotting and clearing before dispatch.'
  })
];

console.log(cards.join(NL));
