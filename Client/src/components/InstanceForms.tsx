import { type FormEvent, useState } from 'react'
import { type DatabaseForm, type DatabasePlace, type MailActions, type MailSettings, saveAddresses, type Settings } from '../api'

const CONFIGURED = "Set by the service's configuration."

/** A value to pass on, such as an invite link or a callback address, with a one-click copy. */
export function CopyValue({ label, value, note }: { label: string; value: string; note?: string }) {
  const [copied, setCopied] = useState(false)
  const [failed, setFailed] = useState(false)

  async function copy() {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(true)
      setFailed(false)
    } catch {
      setFailed(true)
    }
  }

  return (
    <div className="form stacked">
      <label>
        {label}
        <input value={value} readOnly onFocus={(e) => e.target.select()} />
      </label>
      {note && <p className="muted hint">{note}</p>}
      {failed && <p className="error hint">It could not be copied. Select it and copy it yourself.</p>}
      <div className="form-actions">
        <button type="button" className="secondary" onClick={copy}>
          {copied ? 'Copied' : 'Copy'}
        </button>
      </div>
    </div>
  )
}

/** A MySQL database's host, port, name, user, and password. */
export function DatabaseFields({
  place,
  keepsPassword,
  submitLabel,
  onSubmit,
}: {
  place: DatabasePlace | null
  /** Whether a blank password keeps the stored one. */
  keepsPassword: boolean
  submitLabel: string
  onSubmit: (database: DatabaseForm) => Promise<void>
}) {
  const [host, setHost] = useState(place?.host ?? 'localhost')
  const [port, setPort] = useState(String(place?.port ?? 3306))
  const [name, setName] = useState(place?.name ?? 'nexus')
  const [user, setUser] = useState(place?.user ?? 'nexus')
  const [password, setPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await onSubmit({ host: host.trim(), port: Number(port), name: name.trim(), user: user.trim(), password })
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="form" onSubmit={submit}>
      <label>
        Host
        <input value={host} onChange={(e) => setHost(e.target.value)} required autoComplete="off" />
      </label>
      <label>
        Port
        <input type="number" min={1} max={65535} value={port} onChange={(e) => setPort(e.target.value)} required />
      </label>
      <label>
        Database
        <input value={name} onChange={(e) => setName(e.target.value)} required autoComplete="off" />
      </label>
      <label>
        User
        <input value={user} onChange={(e) => setUser(e.target.value)} required autoComplete="off" />
      </label>
      <label>
        Password
        <input type="password" value={password} onChange={(e) => setPassword(e.target.value)} autoComplete="new-password" />
      </label>
      <p className="hint muted wide">
        Nexus makes the database if it is missing and the user may, then brings it up to date.
        {keepsPassword && ' Leave the password blank to keep the stored one.'}
      </p>
      {error && <p className="error wide">{error}</p>}
      <div className="form-actions wide">
        <button type="submit" disabled={busy}>
          {busy ? 'Opening the database…' : submitLabel}
        </button>
      </div>
    </form>
  )
}

/** The client's address, and the address sign-in providers send people back to. */
export function AddressesForm({ settings, onSaved }: { settings: Settings; onSaved: (settings: Settings) => void }) {
  const here = window.location.origin
  const [client, setClient] = useState(settings.clientOrigin ?? here)
  const [callback, setCallback] = useState(settings.callbackOrigin ?? settings.callbackSuggestion)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [saved, setSaved] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    const next = client.trim().replace(/\/+$/, '')
    if (next !== here && !window.confirm(`Pages at ${here} stop reaching Nexus once its client is ${next}. Save anyway?`)) return
    setBusy(true)
    setError(null)
    setSaved(false)
    try {
      onSaved(await saveAddresses(next, callback.trim()))
      setSaved(true)
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="form stacked" onSubmit={submit}>
      <label>
        Client address
        <input
          type="url"
          value={client}
          onChange={(e) => setClient(e.target.value)}
          disabled={settings.clientOriginConfigured}
          required
        />
      </label>
      <p className="hint muted">
        {settings.clientOriginConfigured ? CONFIGURED : 'Where people open Nexus. Mailed links and connect pages point here.'}
      </p>
      <label>
        Callback address
        <input
          type="url"
          value={callback}
          onChange={(e) => setCallback(e.target.value)}
          disabled={settings.callbackOriginConfigured}
          placeholder="https://api.nexus.example.com"
        />
      </label>
      <p className="hint muted">
        {settings.callbackOriginConfigured
          ? CONFIGURED
          : 'The https address of this service, where providers such as Schwab send people back after signing in.'}
      </p>
      {settings.callbackUrl && (
        <CopyValue
          label="Callback to register with each provider"
          value={settings.callbackUrl}
          note="Register this exact address on each provider's app, such as the Schwab app."
        />
      )}
      {error && <p className="error">{error}</p>}
      {saved && <p className="success">Saved.</p>}
      <div className="form-actions">
        <button type="submit" disabled={busy || (settings.clientOriginConfigured && settings.callbackOriginConfigured)}>
          Save addresses
        </button>
      </div>
    </form>
  )
}

type MailPick = 'Postmark' | 'Mailpit' | 'Off'

/** The transport to show first: the one in use, or what the pages last chose. */
function firstPick(mail: MailSettings): MailPick {
  if (mail.transport !== 'None') return mail.transport
  if (mail.choice === 'Off') return 'Off'
  return mail.choice ?? (mail.mailpitConfigured ? 'Mailpit' : 'Postmark')
}

const PICKS: { value: MailPick; label: string }[] = [
  { value: 'Postmark', label: 'Postmark, which delivers to real inboxes' },
  { value: 'Mailpit', label: 'Mailpit, which keeps every message for you to read' },
  { value: 'Off', label: 'Nowhere: mail is off' },
]

/** How the instance sends mail, through Postmark or to Mailpit, or not at all, with a test mail. */
export function MailForm({ mail, actions }: { mail: MailSettings; actions: MailActions }) {
  const [pick, setPick] = useState<MailPick>(() => firstPick(mail))
  const [token, setToken] = useState('')
  const [from, setFrom] = useState(mail.from ?? '')
  const [fromName, setFromName] = useState(mail.fromName)
  const [mailpitUrl, setMailpitUrl] = useState(mail.mailpitUrl ?? '')
  const [recipient, setRecipient] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  async function act(action: () => Promise<void>) {
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      await action()
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    if (pick === 'Off') return
    void act(async () => {
      await actions.save({ transport: pick, serverToken: token.trim(), from: from.trim(), fromName: fromName.trim(), mailpitUrl: mailpitUrl.trim() })
      setToken('')
      setNotice(pick === 'Mailpit' ? 'Mail now goes to Mailpit. Send a test mail to check it.' : 'Saved. Send a test mail to check it.')
    })
  }

  const test = () =>
    act(async () => {
      const sent = await actions.test(recipient.trim())
      setNotice(sent.inbox ? `Sent a test mail to ${sent.sentTo}. Read it in Mailpit at ${sent.inbox}.` : `Sent a test mail to ${sent.sentTo}.`)
    })

  const off = () =>
    window.confirm('Turn mail off? Invites then give a link to pass on, and sign-in links are not offered.') &&
    act(async () => {
      await actions.off()
      setNotice('Mail is off.')
    })

  const locked = mail.transportConfigured

  return (
    <form className="form stacked" onSubmit={submit}>
      {mail.transport === 'Postmark' && (
        <p className="muted">{locked ? "Mail goes through Postmark, as the service's configuration sets it." : 'Mail goes through Postmark.'}</p>
      )}
      {mail.transport === 'Mailpit' && (
        <p className="muted">
          Mail goes to Mailpit, which delivers nothing. It keeps every message for you to read at{' '}
          {mail.mailpitInbox && (
            <a href={mail.mailpitInbox} target="_blank" rel="noreferrer">
              {mail.mailpitInbox}
            </a>
          )}
          .
        </p>
      )}
      {mail.transport === 'None' && (
        <p className="muted">Mail is off. Invites give a link to pass on, and the sign-in page offers only passwords.</p>
      )}
      <fieldset className="choices" disabled={locked}>
        <legend>Send mail through</legend>
        {PICKS.map((p) => (
          <label key={p.value} className="check">
            <input type="radio" name="mail-transport" value={p.value} checked={pick === p.value} onChange={() => setPick(p.value)} />
            {p.label}
          </label>
        ))}
      </fieldset>
      {pick === 'Postmark' && (
        <>
          <label>
            Postmark server token
            <input
              type="password"
              value={token}
              onChange={(e) => setToken(e.target.value)}
              disabled={mail.tokenConfigured || locked}
              placeholder={mail.tokenSet ? 'Stored' : ''}
              autoComplete="off"
            />
          </label>
          <p className="hint muted">
            {mail.tokenConfigured
              ? CONFIGURED
              : mail.tokenSet
                ? 'Leave blank to keep the stored token. Nexus never shows it again.'
                : "Postmark shows it under the server's API Tokens."}
          </p>
        </>
      )}
      {pick === 'Mailpit' && (
        <>
          <label>
            Mailpit's address
            <input
              type="url"
              value={mailpitUrl}
              onChange={(e) => setMailpitUrl(e.target.value)}
              disabled={mail.mailpitConfigured}
              placeholder="http://localhost:8025"
              autoComplete="off"
            />
          </label>
          <p className="hint muted">{mail.mailpitConfigured ? CONFIGURED : 'Where Mailpit runs. Start it with mailpit, and it listens at http://localhost:8025.'}</p>
        </>
      )}
      {pick !== 'Off' && (
        <>
          <label>
            From address{pick === 'Mailpit' && ' (optional)'}
            <input type="email" value={from} onChange={(e) => setFrom(e.target.value)} disabled={mail.fromConfigured || locked} />
          </label>
          <p className="hint muted">
            {mail.fromConfigured ? CONFIGURED : pick === 'Mailpit' ? 'Mailpit takes any address.' : 'On a domain Postmark may send as.'}
          </p>
          <label>
            Sender's name
            <input value={fromName} onChange={(e) => setFromName(e.target.value)} disabled={mail.fromNameConfigured || locked} />
          </label>
        </>
      )}
      {actions.asksForRecipient && mail.transport !== 'None' && (
        <label>
          Send a test mail to
          <input type="email" value={recipient} onChange={(e) => setRecipient(e.target.value)} autoComplete="email" />
        </label>
      )}
      {error && <p className="error">{error}</p>}
      {notice && <p className="success">{notice}</p>}
      <div className="form-actions">
        {pick !== 'Off' && (
          <button type="submit" disabled={busy || locked}>
            Save mail
          </button>
        )}
        {pick === 'Off' && mail.transport !== 'None' && !locked && (
          <button type="button" onClick={off} disabled={busy}>
            Turn mail off
          </button>
        )}
        {mail.transport !== 'None' && (
          <button type="button" className="secondary" onClick={test} disabled={busy || (actions.asksForRecipient && !recipient.trim())}>
            Send a test mail
          </button>
        )}
      </div>
    </form>
  )
}
