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

/** Replaces a value and fails loudly if the pattern was not there to replace. */
function rewrite(path, pattern, replacement, what) {
  const file = join(repoRoot, path);
  const before = readFileSync(file, 'utf8');
  const after = before.replace(pattern, replacement);

  if (after === before) {
    // Silence here would mean shipping a bundle pointed at the wrong host, which fails only
    // in the browser and only for real users.
    throw new Error(`${path}: found nothing to replace for ${what}. Has the file changed shape?`);
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
