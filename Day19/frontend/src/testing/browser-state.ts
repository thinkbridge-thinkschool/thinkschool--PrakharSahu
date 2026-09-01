/**
 * Wipes every place `TokenStore` can leave a session, so one spec cannot sign the next one
 * in.
 *
 * This exists because the leak has now happened twice. First when the session hint lived in
 * `sessionStorage`, and again the moment the access token moved into a cookie: specs that
 * cleared Web Storage kept passing while `quotes_at` survived into the next test, and a
 * "signed out" case quietly asserted nothing. Clearing storage piecemeal in each `beforeEach`
 * means every future change to where tokens live silently breaks a handful of specs. One
 * function, called everywhere, moves with the implementation instead.
 */
export function clearBrowserState(): void {
  sessionStorage.clear();
  localStorage.clear();
  clearCookies();
}

/**
 * There is no `document.cookies.clear()`. The only way to remove a cookie is to set it again
 * with an expiry in the past, and the path must match the one it was written with — hence
 * `Path=/`, which is what everything in this app uses.
 */
function clearCookies(): void {
  for (const entry of document.cookie.split('; ')) {
    const name = entry.split('=')[0];
    if (name) {
      document.cookie = `${name}=; Max-Age=0; Path=/`;
    }
  }
}
