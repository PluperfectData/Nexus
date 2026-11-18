import type { SignIn } from '../api'
import { accountsLine, signInStatus } from '../signIn'
import { LevelIcon } from './LevelIcon'

/** A connection's sign-in: since when, when it ends or how it is kept, and the accounts it reads. */
export function SignInStatus({ signIn, provider }: { signIn: SignIn; provider: string }) {
  const { text, tone } = signInStatus(signIn, provider)
  const accounts = accountsLine(signIn.accounts)
  return (
    <>
      {tone === 'muted' ? (
        <p className="muted">{text}</p>
      ) : (
        <p className={`${tone} with-icon`}>
          <LevelIcon level={tone === 'error' ? 'Error' : 'Warning'} />
          {text}
        </p>
      )}
      {accounts && <p className="muted">{accounts}</p>}
    </>
  )
}
