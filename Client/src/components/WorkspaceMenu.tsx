import { useEffect, useRef, useState } from 'react'
import { Link } from 'react-router'
import type { Workspace } from '../api'
import { workspacePath } from '../workspace'

/**
 * The header's workspace switcher: the workspace the page is in or was last opened, and the others to open, with an
 * admin's way to manage them. The header keys it on the path, so a navigation remounts it closed.
 */
export function WorkspaceMenu({ current, workspaces, isAdmin }: { current: Workspace; workspaces: Workspace[]; isAdmin: boolean }) {
  const [open, setOpen] = useState(false)
  const box = useRef<HTMLDivElement>(null)

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
    <div className="workspace-switch" ref={box}>
      <button
        type="button"
        className="workspace-button"
        title={current.name}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls="workspace-menu"
        onClick={() => setOpen((shown) => !shown)}
      >
        <span className="sr-only">Workspace: </span>
        <span className="workspace-name">{current.name}</span>
        <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false">
          <path d="M6 9l6 6 6-6" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" />
        </svg>
      </button>
      {open && (
        <div className="account-menu workspace-menu" id="workspace-menu" role="menu" aria-label="Workspaces">
          {workspaces.map((workspace) => (
            <Link
              key={workspace.id}
              to={workspacePath(workspace.id)}
              role="menuitem"
              aria-current={workspace.id === current.id ? 'page' : undefined}
            >
              {workspace.name}
            </Link>
          ))}
          {isAdmin && (
            <Link to="/workspaces" role="menuitem" className="menu-foot">
              Manage workspaces
            </Link>
          )}
        </div>
      )}
    </div>
  )
}
