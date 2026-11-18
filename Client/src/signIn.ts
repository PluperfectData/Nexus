import type { SignIn } from './api'

export type SignInTone = 'muted' | 'warning' | 'error'

const count = (n: number, unit: string) => `${n} ${unit}${n === 1 ? '' : 's'}`

/** "Nov 25, 2026, 9:14 AM", in the browser's own zone. */
export const when = (at: string) => new Date(at).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/** "in 7 days", "in 23 hours", "in 5 minutes", or "now". */
export function fromNow(at: string, now = Date.now()) {
  const ms = new Date(at).getTime() - now
  if (ms <= 0) return 'now'
  const minutes = Math.ceil(ms / 60_000)
  if (minutes < 60) return `in ${count(minutes, 'minute')}`
  const hours = Math.round(ms / 3_600_000)
  if (hours < 48) return `in ${count(hours, 'hour')}`
  return `in ${count(Math.round(ms / 86_400_000), 'day')}`
}

/** "a", "a and b", or "a, b, and c". */
export function list(items: string[]) {
  if (items.length <= 2) return items.join(' and ')
  return `${items.slice(0, -1).join(', ')}, and ${items[items.length - 1]}`
}

/** What a connection's card says of its sign-in, and how loudly. */
export function signInStatus(signIn: SignIn, provider: string, now = Date.now()): { text: string; tone: SignInTone } {
  if (signIn.ended) {
    const at = signIn.endedAt ? ` ${when(signIn.endedAt)}` : ''
    return { tone: 'error', text: `Its sign-in ended${at}. Its syncs stop until someone signs in again.` }
  }

  const since = signIn.signedInAt ? `Signed in ${when(signIn.signedInAt)}.` : 'Signed in.'
  if (signIn.kind === 'FixedClock' && signIn.endsAt) {
    const ends = `${provider} ends it ${when(signIn.endsAt)}, ${fromNow(signIn.endsAt, now)}.`
    return signIn.endingSoon
      ? { tone: 'warning', text: `Its sign-in ends soon: ${ends} Sign in again to start its clock over.` }
      : { tone: 'muted', text: `${since} ${ends}` }
  }

  if (signIn.kind === 'KeptAlive') {
    const last = signIn.keptAliveAt ? `, last at ${when(signIn.keptAliveAt)}` : ''
    return { tone: 'muted', text: `${since} Nexus keeps it alive${last}.` }
  }

  return { tone: 'muted', text: `${since} It stays signed in.` }
}

/** "Reads the account …1234.", or null for a sign-in that names none. */
export const accountsLine = (accounts: string[]) =>
  accounts.length === 0 ? null : `Reads the ${accounts.length === 1 ? 'account' : 'accounts'} ${list(accounts)}.`

/** Whether a connection's card offers Sign in again: once it has ended, and early for a fixed clock. */
export const offersSignIn = (signIn: SignIn | null) => signIn !== null && (signIn.ended || signIn.kind === 'FixedClock')
