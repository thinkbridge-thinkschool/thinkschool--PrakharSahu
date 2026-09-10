// =============================================================================================
// Render one real distributed trace as a waterfall PNG.
//
//   node scripts/render-trace.mjs
//
// Reads docs/trace-spans.json — the raw output of an App Insights query, captured by
// scripts/capture-trace.sh — and draws it. Nothing is invented: every span, duration, role and
// parent id comes from the query result.
//
// ---------------------------------------------------------------------------------------------
// WHY THIS EXISTS ALONGSIDE THE PORTAL SCREENSHOT
//
// docs/portal-end-to-end-transaction.jpg is the real thing — the Azure portal's End-to-end
// transaction details blade — and it is the primary evidence.
//
// This is kept for two reasons. It shows each span's own id and parent id side by side, which the
// portal renders as nesting rather than as values, and the parent ids are what actually prove the
// linkage. And it needs nothing but a query, so it works in CI or on a machine whose browser is
// signed into the wrong Entra tenant — which was the case here until the tenant was switched.
// =============================================================================================

import { readFileSync, writeFileSync, mkdirSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const CHROME = 'C:/Program Files/Google/Chrome/Application/chrome.exe';

const raw = JSON.parse(readFileSync(resolve(root, 'docs/trace-spans.json'), 'utf8'));
const table = raw.tables[0];
const cols = table.columns.map(c => c.name);
const spans = table.rows.map(r => Object.fromEntries(cols.map((c, i) => [c, r[i]])));

if (spans.length === 0) {
  console.error('docs/trace-spans.json contains no spans. Run scripts/capture-trace.sh first.');
  process.exit(1);
}

// ---------------------------------------------------------------------------------------------
// Lay the waterfall out on a real time axis.
//
// The x position is the span's own start relative to the trace's start, and the width is its
// duration. That is what makes a waterfall worth looking at rather than a list: two bars at the
// same offset ran concurrently, and a bar starting where another ends is a sequential hop.
//
// The two `consume` bars overlapping is the fan-out being visible — one message copied to two
// subscriptions and handled at the same moment by different consumers.
// ---------------------------------------------------------------------------------------------
const t0 = Math.min(...spans.map(s => Date.parse(s.timestamp)));
const t1 = Math.max(...spans.map(s => Date.parse(s.timestamp) + Number(s.durationMs)));
const total = Math.max(t1 - t0, 1);

// Depth from the parent chain, so the indentation shows the real hierarchy rather than an
// assumed one. A span whose parent is not in this trace sits at depth 0.
const byId = new Map(spans.map(s => [s.id, s]));
const depthOf = (s, guard = 0) => {
  const parent = byId.get(s.parent);
  return !parent || guard > 12 ? 0 : depthOf(parent, guard + 1) + 1;
};

const roleColour = role =>
  role === 'quotes-api' ? { bar: '#6d4aff', chip: '#efeaff', text: '#3b2a99' }
                        : { bar: '#0a9396', chip: '#e0f4f4', text: '#04595b' };

const rows = spans.map(s => {
  const start = Date.parse(s.timestamp) - t0;
  const dur = Number(s.durationMs);
  const c = roleColour(s.role);
  return `
    <tr>
      <td class="role"><span class="chip" style="background:${c.chip};color:${c.text}">${s.role}</span></td>
      <td class="name" style="padding-left:${12 + depthOf(s) * 22}px">
        ${s.name}${s.dep ? `<span class="dep">${s.dep}</span>` : ''}
      </td>
      <td class="bar">
        <div class="track">
          <div class="fill" style="left:${(start / total) * 100}%;width:${Math.max((dur / total) * 100, 0.8)}%;background:${c.bar}"></div>
        </div>
      </td>
      <td class="ms">${dur.toFixed(1)} ms</td>
      <td class="ids"><code>${s.id}</code><br><span>parent <code>${s.parent}</code></span></td>
    </tr>`;
}).join('');

const html = `<!doctype html><meta charset="utf-8">
<style>
  * { box-sizing: border-box; }
  body { margin:0; padding:34px 38px; background:#fff; width:1520px;
         font-family:"Avenir Next LT Pro","Mulish",-apple-system,Segoe UI,sans-serif; color:#171528; }
  h1 { font-size:27px; margin:0 0 4px; font-weight:700; letter-spacing:-.2px; }
  .sub { color:#5d5a72; font-size:15px; margin-bottom:6px; }
  .op { font-family:ui-monospace,Consolas,monospace; font-size:13px; color:#3b2a99; margin-bottom:22px; }
  table { border-collapse:collapse; width:100%; font-size:14.5px; }
  th { text-align:left; font-size:11.5px; text-transform:uppercase; letter-spacing:.9px;
       color:#75718c; font-weight:700; padding:0 10px 9px; border-bottom:2px solid #eceaf4; }
  td { padding:10px; border-bottom:1px solid #f3f1f9; vertical-align:middle; }
  .chip { padding:3px 10px; border-radius:11px; font-size:12px; font-weight:700; white-space:nowrap; }
  .name { font-size:14.5px; }
  .dep { margin-left:9px; font-size:11.5px; color:#75718c; border:1px solid #e2dff0;
         padding:1px 7px; border-radius:9px; }
  .track { position:relative; height:15px; background:#f5f3fb; border-radius:8px; }
  .fill  { position:absolute; height:15px; border-radius:8px; }
  .ms { text-align:right; font-variant-numeric:tabular-nums; white-space:nowrap; font-weight:600; }
  .ids { font-size:10.5px; color:#8b8799; line-height:1.5; }
  .ids code { font-family:ui-monospace,Consolas,monospace; color:#5b5772; }
  .note { margin-top:24px; padding:15px 18px; background:#f8f6ff; border-left:4px solid #6d4aff;
          font-size:14px; line-height:1.65; color:#332f4d; border-radius:0 7px 7px 0; }
  .note b { color:#171528; }
</style>
<h1>One distributed trace — API to worker to database</h1>
<div class="sub">${spans.length} spans across
  ${new Set(spans.map(s => s.role)).size} roles &middot; rendered from Application Insights query output</div>
<div class="op">operation_Id ${spans[0].operation_Id}</div>
<table>
  <tr><th>Role</th><th>Span</th><th>Timeline</th><th>Duration</th><th>Span id / parent</th></tr>
  ${rows}
</table>
<div class="note">
  The hop that matters is <b>outbox.publish</b>: it runs in <b>quotes-worker</b>, a different OS
  process, and its parent is a span from <b>quotes-api</b>. Nothing carried that link
  automatically &mdash; a database row is not a message and has no headers, so the API stored the
  W3C traceparent on the outbox row and the relay restored it. The two
  <b>consume</b> spans below it are the broker fan-out, one per subscription, linked from the
  traceparent the SDK wrote into the message.
</div>`;

mkdirSync(resolve(root, 'docs'), { recursive: true });
const htmlPath = resolve(root, 'docs/distributed-trace.html');
const pngPath = resolve(root, 'docs/distributed-trace.png');
writeFileSync(htmlPath, html, 'utf8');

execFileSync(CHROME, [
  '--headless', '--disable-gpu', '--hide-scrollbars',
  // Height sized to the content. A fixed window leaves a white band under short traces, which
  // reads as a rendering fault rather than as an intentional margin.
  `--window-size=1520,${360 + spans.length * 53}`,
  `--screenshot=${pngPath}`,
  htmlPath
], { stdio: 'ignore' });

console.log(`Rendered ${spans.length} spans -> docs/distributed-trace.png`);
