import { createContext, useContext } from 'react'
import type { Workspace } from './api'

/** The workspaces the signed-in user sees, which the header lists, and a way to read them again after a change. */
export interface Workspaces {
  workspaces: Workspace[] | null
  error: string | null
  refresh: () => void
}

export const WorkspacesContext = createContext<Workspaces>({ workspaces: null, error: null, refresh: () => {} })

export const useWorkspaces = () => useContext(WorkspacesContext)

export const workspacePath = (id: number) => `/workspaces/${id}`

const LAST = 'nexus-workspace'

/** Remembers the workspace last opened in this browser, which `/` opens next time. */
export function rememberWorkspace(id: number) {
  try {
    localStorage.setItem(LAST, String(id))
  } catch {
    // Without storage, `/` opens the first workspace by name.
  }
}

export function lastWorkspace(): number | null {
  try {
    const id = Number(localStorage.getItem(LAST))
    return Number.isInteger(id) && id > 0 ? id : null
  } catch {
    return null
  }
}

/** The workspace the header names: the one the page is in, or else the last opened, or else the first. */
export function currentOf(workspaces: Workspace[] | null, inPage: number | null): Workspace | null {
  if (!workspaces || workspaces.length === 0) return null
  if (inPage !== null) return workspaces.find((workspace) => workspace.id === inPage) ?? null
  const last = lastWorkspace()
  return workspaces.find((workspace) => workspace.id === last) ?? workspaces[0]
}
