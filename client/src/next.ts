const KEY = 'nexus-next'

// Long enough to sign in, or to open a mailed link from another tab; short enough that a later visit lands home.
const LIFETIME_MS = 10 * 60 * 1000

/** Keeps the page someone was sent away from to sign in, so signing in can bring them back. */
export function rememberNext(path: string) {
  try {
    localStorage.setItem(KEY, JSON.stringify({ path, at: Date.now() }))
  } catch {
    // Blocked storage only means signing in lands on the workspaces.
  }
}

/** The page to go back to after signing in, or the workspaces. Reading it leaves it in place. */
export function peekNext(): string {
  try {
    const kept = JSON.parse(localStorage.getItem(KEY) ?? 'null') as { path?: unknown; at?: unknown } | null
    return typeof kept?.path === 'string' && kept.path.startsWith('/') && typeof kept.at === 'number' && Date.now() - kept.at < LIFETIME_MS
      ? kept.path
      : '/'
  } catch {
    return '/'
  }
}

/** Forgets the page, once someone signed in reaches any page. */
export function clearNext() {
  try {
    localStorage.removeItem(KEY)
  } catch {
    // Nothing kept, nothing to forget.
  }
}
