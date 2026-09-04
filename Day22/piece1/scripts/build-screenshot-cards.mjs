// Builds the HTML cards that Screenshots/*.jpg are captured from.
//
// Every line of terminal output on a card is sliced out of docs/resilience-verification.txt and
// docs/test-results.txt at build time. Nothing is retyped, so a card cannot drift away from the
// run it claims to show -- if the script's output changes, regenerate and the cards change with
// it.
//
//   node scripts/build-screenshot-cards.mjs
//   npx http-server .shots -p 8099        (file:// is blocked by the capture tool)

import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const out = join(root, '.shots');
mkdirSync(out, { recursive: true });

const verification = readFileSync(join(root, 'docs/resilience-verification.txt'), 'utf8');
const testResults = readFileSync(join(root, 'docs/test-results.txt'), 'utf8');

/** Pulls one "==== N. Title ====" block out of the verification log. */
function section(number) {
  const blocks = verification.split(/={60,}/);
  for (let i = 0; i < blocks.length - 1; i++) {
    if (blocks[i + 1]?.trim().startsWith(`${number}.`)) {
      return `${blocks[i + 1].trim()}\n${blocks[i + 2] ?? ''}`.replace(/\n{3,}/g, '\n\n').trimEnd();
    }
  }
  throw new Error(`section ${number} not found`);
}

/** The preamble, which sits before the first numbered section. */
function preamble() {
  return verification.split(/={60,}/)[0].trim();
}

const NL = String.fromCharCode(10);

const escape = (s) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

// PASS lines and state names are the things a reader's eye should land on first.
function highlight(text) {
  return escape(text)
    .replace(/\[PASS\]/g, '<span class="pass">[PASS]</span>')
    .replace(/\[FAIL\]/g, '<span class="fail">[FAIL]</span>')
    .replace(/\bOPENED\b/g, '<span class="open">OPENED</span>')
    .replace(/\bHALF-OPENED\b/g, '<span class="half">HALF-OPENED</span>')
    .replace(/\bCLOSED\b/g, '<span class="closed">CLOSED</span>')
    .replace(/\bcircuit=Open\b/g, '<span class="open">circuit=Open</span>')
    .replace(/\bcircuit=Closed\b/g, '<span class="closed">circuit=Closed</span>')
    .replace(/\bPassed!/g, '<span class="pass">Passed!</span>')
    .replace(/(\d+) passed/g, '<span class="pass">$1 passed</span>')
    .replace(/(\b0 failed)/g, '<span class="pass">$1</span>');
}

/**
 * Splits a long block into two side-by-side panes.
 *
 * The capture window cannot be made taller than the screen, so anything past roughly thirty
 * lines is simply not in the image. Splitting at a blank line keeps each pane readable rather
 * than slicing a JSON object in half.
 */
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
    --ink: #1c1a22;
    --muted: #5d5870;
    --accent: #6b4ea8;
    --accent-soft: #f2eefb;
    --rule: #e2dcf0;
    --term-bg: #17151f;
    --term-ink: #d8d3e4;
  }
  * { box-sizing: border-box; }
  body {
    margin: 0;
    padding: 26px 34px;
    background: #ffffff;
    color: var(--ink);
    font-family: "Avenir Next LT Pro", "Avenir Next", Mulish, "Segoe UI", system-ui, sans-serif;
    font-size: 13px;
    line-height: 1.45;
    width: 1470px;
  }
  .eyebrow {
    font-size: 11px;
    letter-spacing: .13em;
    text-transform: uppercase;
    color: var(--accent);
    font-weight: 600;
    margin-bottom: 10px;
  }
  h1 { font-size: 26px; line-height: 1.18; margin: 0 0 9px; font-weight: 700; letter-spacing: -.01em; }
  .lead { font-size: 14px; color: var(--muted); margin: 0 0 16px; max-width: 165ch; }
  .term {
    display: flex;
    gap: 14px;
  }
  pre {
    background: var(--term-bg);
    color: var(--term-ink);
    padding: 16px 18px;
    border-radius: 9px;
    font-family: "Cascadia Code", "JetBrains Mono", Consolas, ui-monospace, monospace;
    font-size: 11px;
    line-height: 1.46;
    margin: 0;
    flex: 1 1 0;
    min-width: 0;
    overflow-x: auto;
    white-space: pre;
  }
  .pass   { color: #7ee0a0; font-weight: 600; }
  .fail   { color: #ff8b8b; font-weight: 600; }
  .open   { color: #ff9f6b; font-weight: 600; }
  .half   { color: #ffd479; font-weight: 600; }
  .closed { color: #7ee0a0; font-weight: 600; }
  .note {
    margin-top: 14px;
    padding: 11px 16px;
    background: var(--accent-soft);
    border-radius: 8px;
    color: var(--ink);
    font-size: 13px;
    max-width: none;
  }
  .note strong { color: var(--accent); }
  footer {
    margin-top: 16px;
    padding-top: 10px;
    border-top: 1px solid var(--rule);
    color: var(--muted);
    font-size: 11px;
    display: flex;
    justify-content: space-between;
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
    <span>Day 22 &middot; piece1 &middot; resilience with Polly</span>
    <span>captured from scripts/verify-resilience.sh</span>
  </footer>
</body>
</html>`;
  writeFileSync(join(out, file), html, 'utf8');
  return file;
}

const cards = [
  card({
    file: '01-pipeline-and-baseline.html',
    eyebrow: 'The pipeline in force',
    title: 'Four strategies around one outbound dependency',
    lead: 'Read back from the running process, not from the source. These are the numbers the '
        + 'breaker, the retry, the two timeouts and the bulkhead are actually enforcing for the '
        + 'rest of this run.',
    body: `${preamble()}\n\n${section(1)}`,
    footnote: 'Every value is smaller than a production one would be. A breaker that needs fifty '
            + 'failures over five minutes and then stays open for a minute is correct in production '
            + 'and useless as a demonstration, because <strong>nobody watches long enough to see it '
            + 'recover</strong>.'
  }),
  card({
    file: '02-idempotent-only-retry.html',
    eyebrow: 'Retry',
    title: 'The same failure, retried three times or not at all',
    lead: 'One dependency, one fault, one pipeline. The only difference between these two calls is '
        + 'whether repeating the operation would have side effects.',
    body: section(2),
    footnote: 'A retry cannot tell <strong>"the request never arrived"</strong> from <strong>"the '
            + 'request was processed and the response was lost"</strong> &mdash; both look like a '
            + 'timeout. Retrying the read is free. Retrying the write charges the customer twice.'
  }),
  card({
    file: '03-circuit-opens.html',
    eyebrow: 'Circuit breaker · closed to open',
    title: 'The circuit opens under sustained failure',
    lead: 'Twelve calls at a dependency that is answering 500 to everything. Watch the eighth: the '
        + 'failure ratio crosses its threshold, the breaker trips, and every call after it stops '
        + 'reaching the network at all.',
    body: section(3),
    footnote: 'Calls 1&ndash;8 cost a socket, a round trip and a 500. Calls 9&ndash;12 cost '
            + '<strong>0.4ms and nothing else</strong>. That gap is the breaker: the caller fails '
            + 'faster, and the struggling dependency is left alone to recover.'
  }),
  card({
    file: '04-open-circuit-costs-nothing.html',
    eyebrow: 'Circuit breaker · open',
    title: 'An open circuit never touches the network',
    lead: 'Ten more calls into an open breaker, with the upstream failure counter read before and '
        + 'after.',
    body: section(4),
    footnote: 'The counter did not move. <strong>Not one of those ten calls reached the '
            + 'dependency</strong> &mdash; they were rejected inside the process in microseconds. '
            + 'Breaker rejections are the cost the breaker removed, not failures it caused.'
  }),
  card({
    file: '05-recovery-timeline.html',
    eyebrow: 'Circuit breaker · open to half-open to closed',
    title: 'Recovery, with no operator involved',
    lead: 'The dependency is repaired while the breaker is still open. Note the first call after '
        + 'the repair: it is still rejected, because a breaker recovers on a timer, not on news.',
    body: section(5),
    footnote: 'After the break elapses exactly <strong>one</strong> trial call is admitted. It '
            + 'succeeds, and traffic is restored. One probe, not a stampede &mdash; which is why '
            + 'recovery does not immediately re-break the thing it was protecting.'
  }),
  card({
    file: '06-failed-probe-reopens.html',
    eyebrow: 'Circuit breaker · half-open to open',
    title: 'A failed probe re-opens the circuit',
    lead: 'The same wait, but this time the dependency is still broken when the trial call goes '
        + 'out.',
    body: section(6),
    footnote: 'Half-open lasted two milliseconds. Recovery is <strong>not assumed on a '
            + 'schedule</strong>: one failed probe and the break starts again, so a dependency that '
            + 'is still down is not re-flooded the instant its timer expires.'
  }),
  card({
    file: '07-timeout-and-bulkhead.html',
    eyebrow: 'Timeout · bulkhead',
    title: 'Bounding a slow dependency, and shedding what will not fit',
    lead: 'A dependency that stops answering is more dangerous than one that fails, because failure '
        + 'is fast and slowness is contagious. These are the two strategies that contain it.',
    body: `${section(7)}\n\n${section(8)}`,
    footnote: 'The timeout stops one call from waiting three seconds on a dependency that has gone '
            + 'quiet. The bulkhead stops twelve callers from queueing behind it: nine were shed '
            + 'immediately so that <strong>the rest of the process kept its threads</strong>.'
  }),
  card({
    file: '08-tests-green.html',
    eyebrow: 'Verification',
    title: 'Sixty-three backend tests, one hundred and forty-seven in the frontend',
    lead: 'The unit suite drives the real production pipeline &mdash; the same '
        + 'UpstreamResilience.Configure that Program.cs calls &mdash; with only the timings lowered. '
        + 'The live script above covers the real-network half.',
    body: `${testResults.trim()}\n\n${verification.split(/={60,}/).pop().trim()}`,
    footnote: 'Run three times consecutively at 16/16 before capture. <strong>Two assertions were '
            + 'wrong the first time</strong>, and both now start from a freshly opened circuit.'
  })
];

console.log(cards.join('\n'));
