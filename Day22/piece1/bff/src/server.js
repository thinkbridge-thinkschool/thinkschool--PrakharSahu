import express from 'express';

import { ALLOWED_ORIGINS, PORT, UPSTREAM_API_BASE } from './config.js';
import { describeCallerToken, getCallerToken } from './token-broker.js';

const app = express();

/**
 * The body arrives as an opaque Buffer and is forwarded byte-for-byte.
 *
 * Parsing it as JSON and re-serialising would be lossy for anything this proxy does not
 * understand, and would quietly change payloads the upstream validates. A proxy has no
 * business reading the mail.
 */
app.use(express.raw({ type: () => true, limit: '1mb' }));

/**
 * Headers that describe a single network hop rather than the message.
 *
 * Forwarding these corrupts the second hop: a `content-length` copied from the inbound
 * request stops matching once the body is re-encoded, and `host` pointed at this proxy
 * makes the upstream generate self-referential URLs.
 */
const HOP_BY_HOP = new Set([
  'connection',
  'keep-alive',
  'transfer-encoding',
  'upgrade',
  'proxy-authorization',
  'proxy-authenticate',
  'te',
  'trailer',
  'host',
  'content-length',
]);

/**
 * Cross-origin access for the Static Web App.
 *
 * This tier exists because the browser sits on `*.azurestaticapps.net` while the API sits on
 * `*.azurecontainerapps.io` — different sites, so nothing is same-origin and every call is
 * a credentialed cross-origin request. The origin is echoed rather than wildcarded because
 * `Access-Control-Allow-Origin: *` is incompatible with `Allow-Credentials: true`; browsers
 * drop the response entirely rather than downgrading.
 */
app.use((req, res, next) => {
  const origin = req.headers.origin;

  if (origin && ALLOWED_ORIGINS.includes(origin.replace(/\/+$/, ''))) {
    res.setHeader('Access-Control-Allow-Origin', origin);
    res.setHeader('Access-Control-Allow-Credentials', 'true');
  }
  // Set unconditionally: caches must not serve one origin's response to another, and that
  // is true even when the origin was rejected and no CORS headers were written.
  res.setHeader('Vary', 'Origin');

  if (req.method === 'OPTIONS') {
    res.setHeader('Access-Control-Allow-Methods', 'GET,POST,PUT,PATCH,DELETE,OPTIONS');
    res.setHeader('Access-Control-Allow-Headers', 'Authorization,Content-Type');
    res.setHeader('Access-Control-Max-Age', '600');
    return res.status(204).end();
  }

  return next();
});

/** Liveness only. Deliberately says nothing about the upstream or the identity. */
app.get('/healthz', (_req, res) => res.status(200).json({ status: 'ok' }));

/**
 * Proof-of-identity endpoint for the Day-17 verification log.
 *
 * Returns the claims of the token this proxy is currently presenting to the API, so the
 * managed-identity hop can be observed from outside rather than taken on trust. It returns
 * claims, never the token: see `describeCallerToken`.
 */
app.get('/bff/identity', async (_req, res) => {
  try {
    res.status(200).json(await describeCallerToken());
  } catch (failure) {
    res.status(503).json({ error: 'no-managed-identity', detail: String(failure.message ?? failure) });
  }
});

/**
 * Rewrites upstream cookies so they survive the cross-site hop to the Static Web App.
 *
 * The API sets its refresh cookie `SameSite=Strict` (AuthEndpoints.cs), which is correct
 * when the browser and the API share a site — and fatal here, where they do not: a Strict
 * cookie is never attached to a request initiated from another site, so every refresh would
 * arrive without one and fail as though the session had expired.
 *
 * `SameSite=None` is a genuine loosening, not a formality. It makes the cookie a
 * third-party cookie, which means Safari's tracking prevention drops it outright and
 * Chrome's phase-out will eventually do the same. The same-origin alternative needs the
 * Standard plan; this is the documented cost of staying on Free. See Day17/VERIFICATION.md.
 */
function rewriteCookieForCrossSite(cookie) {
  const withoutSameSite = cookie.replace(/;\s*SameSite\s*=\s*(Strict|Lax|None)/gi, '');
  const secured = /;\s*Secure/i.test(withoutSameSite) ? withoutSameSite : `${withoutSameSite}; Secure`;
  return `${secured}; SameSite=None`;
}

/**
 * The proxy.
 *
 * Two identities travel on one request, and keeping them in separate headers is the whole
 * design:
 *
 *   Authorization    the end user's QuotesApi JWT, passed through untouched. The API's
 *                    ownership checks read `sub` from it, so rewriting it would silently
 *                    reassign every quote to the managed identity.
 *   X-Caller-Token   this tier's managed-identity token, proving the request came through
 *                    our front door. The API rejects `/api/*` without it.
 *
 * Putting the managed-identity token in `Authorization` instead would have been more
 * conventional and was the first thing tried; it collapses the two identities into one and
 * breaks per-user authorization on every write. See Day17/VERIFICATION.md.
 */
app.use('/api', async (req, res) => {
  let callerToken;
  try {
    callerToken = await getCallerToken();
  } catch (failure) {
    // 503, not 401: nothing is wrong with the caller's credentials. This tier could not
    // present its own, which is an availability problem on our side.
    return res.status(503).json({
      error: 'managed-identity-unavailable',
      detail: String(failure.message ?? failure),
    });
  }

  const target = `${UPSTREAM_API_BASE}/api${req.url}`;

  const headers = new Headers();
  for (const [name, value] of Object.entries(req.headers)) {
    if (!HOP_BY_HOP.has(name.toLowerCase()) && typeof value === 'string') {
      headers.set(name, value);
    }
  }
  headers.set('X-Caller-Token', `Bearer ${callerToken}`);
  // Lets the upstream log the real client rather than this proxy's egress address.
  headers.set('X-Forwarded-Host', req.headers.host ?? '');
  headers.set('X-Forwarded-Proto', 'https');

  const hasBody = !['GET', 'HEAD', 'OPTIONS'].includes(req.method);

  let upstream;
  try {
    upstream = await fetch(target, {
      method: req.method,
      headers,
      body: hasBody && req.body?.length ? req.body : undefined,
      redirect: 'manual',
    });
  } catch (failure) {
    return res.status(502).json({
      error: 'upstream-unreachable',
      detail: String(failure.message ?? failure),
    });
  }

  for (const [name, value] of upstream.headers) {
    // set-cookie is handled below; copying it from this iterator would flatten multiple
    // cookies into one comma-joined header that no browser parses correctly.
    if (name.toLowerCase() === 'set-cookie' || HOP_BY_HOP.has(name.toLowerCase())) continue;
    // The CORS headers already written for this response win over anything upstream says.
    if (name.toLowerCase().startsWith('access-control-')) continue;
    res.setHeader(name, value);
  }

  const cookies = upstream.headers.getSetCookie?.() ?? [];
  if (cookies.length > 0) {
    res.setHeader('Set-Cookie', cookies.map(rewriteCookieForCrossSite));
  }

  res.status(upstream.status);

  // 204 and 304 carry no body, and writing one makes the response invalid. The API answers
  // 204 on DELETE and on PUT /author, so this is the normal path, not an edge case.
  if (upstream.status === 204 || upstream.status === 304) {
    return res.end();
  }

  return res.send(Buffer.from(await upstream.arrayBuffer()));
});

app.listen(PORT, () => {
  console.log(
    `quotes-bff listening on :${PORT} -> ${UPSTREAM_API_BASE} ` +
      `(origins: ${ALLOWED_ORIGINS.join(', ')})`,
  );
});
