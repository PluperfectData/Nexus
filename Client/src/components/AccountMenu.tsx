import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import type { Me } from '../api'

/**
 * The signed-in user's menu, at the header's far right: who they are, their account page, an admin's Users,
 * Workspaces, and Settings pages, and signing out. The header keys it on the path, so a navigation remounts it closed.
 */
export function AccountMenu({ me, onSignOut }: { me: Me; onSignOut: () => void }) {
  const [open, setOpen] = useState(false)
  const box = useRef<HTMLDivElement>(null)
  const who = `${me.email}, ${me.isAdmin ? 'Admin' : 'Member'}`

  useEffect(() => {
    if (!open) return
    const outside = (event: PointerEvent) => {
      if (!box.current?.contains(event.target as Node)) setOpen(false)
    }
    const escape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setOpen(false)
    }
    document.addEventListener('pointerdown', outside)
    document.addEventListener('keydown', escape)
    return () => {
      document.removeEventListener('pointerdown', outside)
      document.removeEventListener('keydown', escape)
    }
  }, [open])

  return (
    <div className="account" ref={box}>
      <button
        type="button"
        className="icon-button"
        title={who}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls="account-menu"
        onClick={() => setOpen((current) => !current)}
      >
        <span className="sr-only">Account menu for {who}</span>
        <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
          <g fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round">
            <circle cx="12" cy="12" r="9.5" />
            <circle cx="12" cy="10" r="3.2" />
            <path d="M6.2 18.6c1.3-2.3 3.4-3.6 5.8-3.6s4.5 1.3 5.8 3.6" />
          </g>
        </svg>
      </button>
      {open && (
        <div className="account-menu" id="account-menu" role="menu" aria-label="Account">
          <p className="account-who">
            <strong>{me.email}</strong>
            <span className="muted">{me.isAdmin ? 'Admin' : 'Member'}</span>
          </p>
          <Link to="/account" role="menuitem">
            Account
          </Link>
          {me.isAdmin && (
            <>
              <Link to="/users" role="menuitem">
                Users
              </Link>
              <Link to="/workspaces" role="menuitem">
                Workspaces
              </Link>
              <Link to="/settings" role="menuitem">
                Settings
              </Link>
            </>
          )}
          <button type="button" role="menuitem" onClick={onSignOut}>
            Sign out
          </button>
        </div>
      )}
    </div>
  )
}
