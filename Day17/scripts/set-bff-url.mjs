#!/usr/bin/env node
/**
 * Stamps the deployed BFF hostname into the two frontend files that need to know it.
 *
 * The hostname is only knowable after the Container App exists, and it changes whenever the
 * environment is recreated. Hand-editing two files in two different syntaxes after every
 * deploy is exactly the step someone forgets, and the failure is quiet: the app builds, the
 * page loads, and every API call is blocked by a Content-Security-Policy that still names
 * yesterday's host. So the deploy script calls this instead.
 *
 *   node scripts/set-bff-url.mjs https://quotes-bff.icycliff-1234.centralindia.azurecontainerapps.io
 */

import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = join(dirname(fileURLToPath(import.meta.url)), '..');

const raw = process.argv[2];
if (!raw) {
  console.error('usage: node scripts/set-bff-url.mjs <https://bff-hostname>');
  process.exit(1);
}

let origin;
try {
  const parsed = new URL(raw);
  if (parsed.protocol !== 'https:') {
    throw new Error('must be https — the refresh cookie is Secure and will not travel over http');
  }
  // Origin only. A trailing path here would end up doubled as `/api/api`, and the CSP
  // directive would be silently ignored because connect-src takes hosts, not paths.
  origin = parsed.origin;
} catch (failure) {
  console.error(`Not a usable BFF URL: ${failure.message ?? failure}`);
  process.exit(1);
}

/**
 * Replaces a value and fails loudly if the pattern was not there to replace.
 *
 * The failure condition is "the pattern did not match", NOT "the file did not change". Those
 * look identical from the outside and are opposites in meaning: the first means the file has
 * changed shape and the bundle would ship pointed at the wrong host, the second means the
 * value is already correct and there is nothing to do.
 *
 * Conflating them made this script fail exactly when it had nothing to fix. The committed
 * environment.production.ts already carries the current BFF hostname — because the last
 * deploy stamped it there and it was committed — so re-running produced a byte-identical
 * result and threw "found nothing to replace" on a perfectly healthy repository. CI hit it on
 * the first green-path run.
 */
function rewrite(path, pattern, replacement, what) {
  const file = join(repoRoot, path);
  const before = readFileSync(file, 'utf8');

  // Neither pattern is global, so `.test` does not carry lastIndex state between calls.
  if (!pattern.test(before)) {
    throw new Error(`${path}: found nothing to replace for ${what}. Has the file changed shape?`);
  }

  const after = before.replace(pattern, replacement);

  if (after === before) {
    console.log(`  ${path}  ->  ${what} already correct, left alone`);
    return;
  }

  writeFileSync(file, after);
  console.log(`  ${path}  ->  ${what}`);
}

console.log(`Stamping BFF origin ${origin}`);

rewrite(
  'frontend/src/environments/environment.production.ts',
  /apiBaseUrl: '[^']*'/,
  `apiBaseUrl: '${origin}/api'`,
  'apiBaseUrl',
);

rewrite(
  'frontend/public/staticwebapp.config.json',
  /connect-src 'self' https:\/\/[^;"]*/,
  `connect-src 'self' ${origin}`,
  'CSP connect-src',
);

console.log('Done.');
