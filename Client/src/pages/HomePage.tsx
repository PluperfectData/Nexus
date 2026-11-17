import { Navigate } from 'react-router'
import { currentOf, useWorkspaces, workspacePath } from '../workspace'

/** Opens the workspace last opened in this browser, or the first by name. With none, the Workspaces page. */
export function HomePage() {
  const { workspaces, error } = useWorkspaces()

  if (error) return <p className="page error">{error}</p>
  if (workspaces === null) return <p className="page muted">Loading…</p>

  const open = currentOf(workspaces, null)
  return <Navigate to={open ? workspacePath(open.id) : '/workspaces'} replace />
}
