import { type FormEvent, useEffect, useState } from 'react'
import { getReminderEmail, saveReminderEmail, type ReminderEmail, type ReminderEmailChoice } from '../api'

const CHOICES: { value: ReminderEmailChoice; label: string }[] = [
  { value: 'Off', label: 'No one' },
  { value: 'Everyone', label: 'Every member and admin' },
  { value: 'Admins', label: 'The admins' },
  { value: 'Named', label: 'People I pick' },
]

/** Who Nexus mails when one of the workspace's sign-ins is about to end, and again once it has. */
export function ReminderEmailSection({ workspaceId }: { workspaceId: number }) {
  const [mail, setMail] = useState<ReminderEmail | null>(null)
  const [choice, setChoice] = useState<ReminderEmailChoice>('Off')
  const [people, setPeople] = useState<number[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const show = (found: ReminderEmail) => {
    setMail(found)
    setChoice(found.choice)
    setPeople(found.people)
  }

  useEffect(() => {
    getReminderEmail(workspaceId)
      .then(show)
      .catch((e: Error) => setError(e.message))
  }, [workspaceId])

  async function submit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      show(await saveReminderEmail(workspaceId, choice, choice === 'Named' ? people : []))
      setNotice('Saved.')
    } catch (e) {
      setError((e as Error).message)
    } finally {
      setBusy(false)
    }
  }

  const toggle = (id: number, on: boolean) => setPeople((current) => (on ? [...current, id] : current.filter((p) => p !== id)))

  return (
    <section>
      <div className="section-head">
        <h2>Session expiry reminder email</h2>
      </div>
      <p className="muted">Who Nexus mails when a connection's sign-in is about to end, and again once it has ended.</p>
      {mail && !mail.mailReady && (
        <p className="muted">This instance has no mail set up, so nothing is sent. An admin sets it up on the Settings page.</p>
      )}
      {mail && (
        <form className="form panel" onSubmit={submit}>
          <fieldset className="wide choices">
            <legend>Mail</legend>
            {CHOICES.map((c) => (
              <label key={c.value} className="check">
                <input type="radio" name="reminder-email" value={c.value} checked={choice === c.value} onChange={() => setChoice(c.value)} />
                {c.label}
              </label>
            ))}
          </fieldset>
          {choice === 'Named' && (
            <fieldset className="wide choices">
              <legend>People</legend>
              {mail.candidates.map((person) => (
                <label key={person.id} className="check">
                  <input type="checkbox" checked={people.includes(person.id)} onChange={(e) => toggle(person.id, e.target.checked)} />
                  {person.email}
                  {person.isAdmin && <span className="muted">(admin)</span>}
                </label>
              ))}
            </fieldset>
          )}
          {error && <p className="error wide">{error}</p>}
          {notice && <p className="success wide">{notice}</p>}
          <div className="form-actions wide">
            <button type="submit" disabled={busy}>
              Save
            </button>
          </div>
        </form>
      )}
      {!mail && error && <p className="error">{error}</p>}
    </section>
  )
}
