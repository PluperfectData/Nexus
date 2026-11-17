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

/** Postmark's server token and the sender, with a test mail to the admin. */
export function MailForm({ mail, actions }: { mail: MailSettings; actions: MailActions }) {
  const [token, setToken] = useState('')
  const [from, setFrom] = useState(mail.from ?? '')
  const [fromName, setFromName] = useState(mail.fromName)
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
    void act(async () => {
      await actions.save(token.trim(), from.trim(), fromName.trim())
      setToken('')
      setNotice('Saved. Send a test mail to check it.')
    })
  }

  const test = () =>
    act(async () => {
      const sent = await actions.test(recipient.trim())
      setNotice(sent.folder ? `Wrote a test mail for ${sent.sentTo} in ${sent.folder}.` : `Sent a test mail to ${sent.sentTo}.`)
    })

  // In a debug build, turning Postmark off sends mail back to its files.
  const off = () =>
    window.confirm(
      mail.folder
        ? `Stop sending through Postmark? Mail goes to files in ${mail.folder} again.`
        : 'Turn mail off? Invites then give a link to pass on, and sign-in links are not offered.',
    ) &&
    act(async () => {
      await actions.off()
      setFrom('')
      setNotice(mail.folder ? 'Mail goes to files again.' : 'Mail is off.')
    })

  const stored = mail.tokenSet && !mail.tokenConfigured

  return (
    <form className="form stacked" onSubmit={submit}>
      {mail.transport === 'Files' && (
        <p className="muted">
          This debug build writes mail to files in {mail.folder}. To test real mail, save a Postmark token and sender below.
        </p>
      )}
      {mail.transport === 'Postmark' && mail.folder && (
        <p className="muted">Mail goes through Postmark. Use mail files again to write it to {mail.folder} instead.</p>
      )}
      {mail.transport === 'None' && (
        <p className="muted">
          Mail is off. Invites give a link to pass on, and the sign-in page offers only passwords.
        </p>
      )}
      <label>
        Postmark server token
        <input
          type="password"
          value={token}
          onChange={(e) => setToken(e.target.value)}
          disabled={mail.tokenConfigured}
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
      <label>
        From address
        <input type="email" value={from} onChange={(e) => setFrom(e.target.value)} disabled={mail.fromConfigured} />
      </label>
      <p className="hint muted">{mail.fromConfigured ? CONFIGURED : 'On a domain Postmark may send as.'}</p>
      <label>
        Sender's name
        <input value={fromName} onChange={(e) => setFromName(e.target.value)} disabled={mail.fromNameConfigured} />
      </label>
      {actions.asksForRecipient && mail.transport !== 'None' && (
        <label>
          Send a test mail to
          <input type="email" value={recipient} onChange={(e) => setRecipient(e.target.value)} autoComplete="email" />
        </label>
      )}
      {error && <p className="error">{error}</p>}
      {notice && <p className="success">{notice}</p>}
      <div className="form-actions">
        <button type="submit" disabled={busy || (mail.tokenConfigured && mail.fromConfigured && mail.fromNameConfigured)}>
          Save mail
        </button>
        {mail.transport !== 'None' && (
          <button type="button" className="secondary" onClick={test} disabled={busy || (actions.asksForRecipient && !recipient.trim())}>
            Send a test mail
          </button>
        )}
        {stored && (
          <button type="button" className="quiet" onClick={off} disabled={busy}>
            {mail.folder ? 'Use mail files again' : 'Turn mail off'}
          </button>
        )}
      </div>
    </form>
  )
}
