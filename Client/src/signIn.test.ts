import type { SignIn } from './api'
import { accountsLine, fromNow, list, offersSignIn, signInStatus, when } from './signIn'

const NOW = Date.parse('2026-11-18T15:00:00Z')

const base: SignIn = {
  kind: 'Lasting',
  signedInAt: '2026-11-18T14:00:00Z',
  endsAt: null,
  endingSoon: false,
  ended: false,
  endedAt: null,
  keptAliveAt: null,
  accounts: [],
}

it('says how far off a time is, in the largest unit that reads well', () => {
  expect(fromNow('2026-11-25T15:00:00Z', NOW)).toBe('in 7 days')
  expect(fromNow('2026-11-19T14:00:00Z', NOW)).toBe('in 23 hours')
  expect(fromNow('2026-11-18T15:01:00Z', NOW)).toBe('in 1 minute')
  expect(fromNow('2026-11-18T14:00:00Z', NOW)).toBe('now')
})

it('says each kind of sign-in its own way', () => {
  const since = `Signed in ${when(base.signedInAt!)}.`

  expect(signInStatus(base, 'Stripe', NOW)).toEqual({ tone: 'muted', text: `${since} It stays signed in.` })
  expect(signInStatus({ ...base, kind: 'KeptAlive', keptAliveAt: '2026-11-18T14:59:00Z' }, 'Interactive Brokers', NOW)).toEqual({
    tone: 'muted',
    text: `${since} Nexus keeps it alive, last at ${when('2026-11-18T14:59:00Z')}.`,
  })
  expect(signInStatus({ ...base, kind: 'FixedClock', endsAt: '2026-11-25T14:00:00Z' }, 'Charles Schwab', NOW)).toEqual({
    tone: 'muted',
    text: `${since} Charles Schwab ends it ${when('2026-11-25T14:00:00Z')}, in 7 days.`,
  })
  expect(signInStatus({ ...base, kind: 'KeptAlive', ended: true, endedAt: null }, 'Interactive Brokers', NOW)).toEqual({
    tone: 'error',
    text: 'Its sign-in ended. Its syncs stop until someone signs in again.',
  })
})

it('offers Sign in again once a sign-in ends, and early only for a fixed clock', () => {
  expect(offersSignIn(null)).toBe(false)
  expect(offersSignIn(base)).toBe(false)
  expect(offersSignIn({ ...base, ended: true })).toBe(true)
  expect(offersSignIn({ ...base, kind: 'FixedClock' })).toBe(true)
})

it('names the accounts a sign-in reads', () => {
  expect(accountsLine([])).toBeNull()
  expect(accountsLine(['…1234'])).toBe('Reads the account …1234.')
  expect(accountsLine(['…1234', '…5678', '…9012'])).toBe('Reads the accounts …1234, …5678, and …9012.')
  expect(list(['a', 'b'])).toBe('a and b')
})
