import { type ReactNode, useCallback, useEffect, useMemo, useState } from 'react'
import { listWorkspaces, type Workspace } from '../api'
import { useSession } from '../session'
import { WorkspacesContext } from '../workspace'

/** Reads the signed-in user's workspaces, and again whenever a page says they changed. */
export function WorkspacesProvider({ children }: { children: ReactNode }) {
  const { me } = useSession()
  const userId = me?.signedIn ? me.id : null
  const [loaded, setLoaded] = useState<{ userId: number; workspaces: Workspace[] } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [reads, setReads] = useState(0)

  useEffect(() => {
    if (userId === null) return
    let current = true
    listWorkspaces()
      .then((workspaces) => {
        if (!current) return
        setLoaded({ userId, workspaces })
        setError(null)
      })
      .catch((e: Error) => current && setError(e.message))
    return () => {
      current = false
    }
  }, [userId, reads])

  const refresh = useCallback(() => setReads((count) => count + 1), [])
  const value = useMemo(
    () => ({ workspaces: loaded !== null && loaded.userId === userId ? loaded.workspaces : null, error, refresh }),
    [loaded, userId, error, refresh],
  )

  return <WorkspacesContext.Provider value={value}>{children}</WorkspacesContext.Provider>
}
