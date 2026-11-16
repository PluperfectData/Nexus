import { createAuthClient } from '@pluperfect/jwt'

export const apiUrl = import.meta.env.VITE_API_URL

let signedOut: () => void = () => {}

/** Sets what happens when the session ends for good: a refresh was tried and refused. */
export function onSignedOut(listener: () => void) {
  signedOut = listener
}

// Holds no credential: the session is in HttpOnly cookies. It sends the CSRF header and refreshes an expired session once.
const authFetch = createAuthClient({ baseUrl: apiUrl, onSignedOut: () => signedOut() })

/** A refused request, with the service's own words for why. */
export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

function init(method: string, body: unknown, extra: Record<string, string> = {}): RequestInit {
  const headers: Record<string, string> = { accept: 'application/json', ...extra }
  if (body !== undefined) headers['content-type'] = 'application/json'
  return { method, headers, body: body === undefined ? undefined : JSON.stringify(body) }
}

async function read<T>(response: Response): Promise<T> {
  const json = response.headers.get('content-type')?.includes('json') ? await response.json() : null
  if (!response.ok) {
    throw new ApiError(response.status, json?.detail ?? json?.title ?? `The service answered ${response.status}.`)
  }
  return json as T
}

const request = async <T>(path: string, method = 'GET', body?: unknown): Promise<T> => read<T>(await authFetch(path, init(method, body)))

// A form carrying a file sets its own multipart content type, so none is set here.
const formRequest = async <T>(path: string, method: string, form: FormData): Promise<T> =>
  read<T>(await authFetch(path, { method, headers: { accept: 'application/json' }, body: form }))

// A connect page has no session: its token is its only credential, so it goes in a header on a plain fetch.
const connectRequest = async <T>(token: string, path: string, method = 'GET', body?: unknown): Promise<T> =>
  read<T>(await fetch(`${apiUrl}${path}`, init(method, body, { authorization: `Bearer ${token}` })))

const connectForm = async <T>(token: string, path: string, form: FormData): Promise<T> =>
  read<T>(await fetch(`${apiUrl}${path}`, { method: 'POST', headers: { accept: 'application/json', authorization: `Bearer ${token}` }, body: form }))

/** A connection's name, connector, settings, and file, as the service takes them from a form. */
function connectionForm(name: string, settings: Record<string, string>, file: File, connector?: string): FormData {
  const form = new FormData()
  form.append('name', name)
  if (connector) form.append('connector', connector)
  form.append('settings', JSON.stringify(settings))
  form.append('file', file)
  return form
}

export interface Me {
  signedIn: boolean
  id: number | null
  email: string | null
  isAdmin: boolean
  hasPassword: boolean
}

export interface Workspace {
  id: number
  name: string
  createdAt: string
}

export interface WorkspaceDetail extends Workspace {
  members: { id: number; email: string }[]
}

export interface User {
  id: number
  email: string
  isAdmin: boolean
  hasPassword: boolean
  createdAt: string
  signedInAt: string | null
  workspaces: number[]
}

/** Where an app's rows go. The key fields are what its push API said of the key at the last check. */
export interface Destination {
  id: number
  url: string
  createdAt: string
  keyName: string | null
  keyPrefix: string | null
  keyCheckedAt: string | null
  keyProblem: string | null
}

export interface App {
  id: number
  name: string
  keyPrefix: string | null
  keyCreatedAt: string | null
  returnUrls: string[]
  createdAt: string
  destinations: Destination[]
}

export interface AppWithKey {
  app: App
  key: string
}

export type SettingKind = 'Text' | 'Secret' | 'Number' | 'Boolean' | 'Choice' | 'File'

export interface Field {
  name: string
  label: string
  kind: SettingKind
  required: boolean
  choices: string[] | null
  /** The value the connector reads when the field is left out, which a form starts from. */
  default: string | null
}

export interface Connector {
  id: string
  name: string
  description: string
  auth: string
  fields: Field[]
}

export interface Connection {
  id: number
  name: string
  connector: string
  settings: Record<string, string | null>
  /** The file a connector such as CSV reads, by the name it was uploaded with. */
  fileName: string | null
  needsSignIn: boolean
  createdAt: string
}

export type SyncMode = 'FullRefresh' | 'Incremental'

export interface Stream {
  name: string
  columns: { name: string; type: string }[]
  primaryKey: string[]
  cursorFields: string[]
  syncModes: SyncMode[]
}

export interface Run {
  id: number
  attempt: number
  status: 'Running' | 'Succeeded' | 'Failed'
  startedAt: string
  endedAt: string | null
  rows: number
  errorKind: string | null
  error: string | null
}

/** A sync. Its destination's fields are null once that destination is removed. */
export interface Sync {
  id: number
  connectionId: number
  connectionName: string
  connectorName: string
  needsSignIn: boolean
  destinationId: number | null
  destinationUrl: string | null
  appName: string | null
  destinationKeyName: string | null
  destinationKeyPrefix: string | null
  stream: string
  table: string
  mode: SyncMode
  cursorField: string | null
  status: 'Ready' | 'Queued' | 'Running' | 'Failed'
  lastRun: Run | null
}

export interface SyncPick {
  stream: string
  table: string
  mode: SyncMode
}

export interface SyncEdit {
  destinationId: number
  table: string
  mode: SyncMode
  cursorField: string | null
}

export const getMe = () => request<Me>('/auth/me')
export const signIn = (email: string, password: string, remember: boolean) =>
  request<Me>('/auth/sign-in', 'POST', { email, password, remember })
export const mailLink = (email: string) => request<null>('/auth/link', 'POST', { email })
export const redeemLink = (token: string, remember: boolean) => request<Me>('/auth/link/redeem', 'POST', { token, remember })
export const signOut = () => request<null>('/auth/sign-out', 'POST')
export const setPassword = (password: string) => request<Me>('/auth/password', 'PUT', { password })

export const listUsers = () => request<User[]>('/users')
export const inviteUser = (email: string, isAdmin: boolean) => request<User>('/users', 'POST', { email, isAdmin })
export const reinviteUser = (id: number) => request<null>(`/users/${id}/invite`, 'POST')
export const editUser = (id: number, isAdmin: boolean) => request<User>(`/users/${id}`, 'PATCH', { isAdmin })
export const removeUser = (id: number) => request<null>(`/users/${id}`, 'DELETE')

export const listWorkspaces = () => request<Workspace[]>('/workspaces')
export const getWorkspace = (id: number) => request<WorkspaceDetail>(`/workspaces/${id}`)
export const makeWorkspace = (name: string) => request<WorkspaceDetail>('/workspaces', 'POST', { name })
export const removeWorkspace = (id: number) => request<null>(`/workspaces/${id}`, 'DELETE')
export const addMember = (workspaceId: number, userId: number) =>
  request<WorkspaceDetail>(`/workspaces/${workspaceId}/members/${userId}`, 'PUT')
export const removeMember = (workspaceId: number, userId: number) =>
  request<WorkspaceDetail>(`/workspaces/${workspaceId}/members/${userId}`, 'DELETE')

export const listApps = (workspaceId: number) => request<App[]>(`/workspaces/${workspaceId}/apps`)
export const makeApp = (workspaceId: number, name: string, returnUrls: string[]) =>
  request<AppWithKey>(`/workspaces/${workspaceId}/apps`, 'POST', { name, returnUrls })
export const editApp = (id: number, name: string, returnUrls: string[]) => request<App>(`/apps/${id}`, 'PATCH', { name, returnUrls })
export const newAppKey = (id: number) => request<AppWithKey>(`/apps/${id}/key`, 'POST')
export const revokeAppKey = (id: number) => request<App>(`/apps/${id}/key`, 'DELETE')
export const removeApp = (id: number) => request<null>(`/apps/${id}`, 'DELETE')
export const addDestination = (appId: number, url: string, key: string) =>
  request<Destination>(`/apps/${appId}/destinations`, 'POST', { url, key })
export const checkDestination = (id: number) => request<Destination>(`/destinations/${id}/check`, 'POST')
export const removeDestination = (id: number) => request<null>(`/destinations/${id}`, 'DELETE')

export const listConnectors = () => request<Connector[]>('/connectors')
export const listConnections = (workspaceId: number) => request<Connection[]>(`/workspaces/${workspaceId}/connections`)
/** Adds a connection once its settings connect, uploading its file when its connector reads one. */
export const addConnection = (workspaceId: number, name: string, connector: string, settings: Record<string, string>, file?: File) =>
  file
    ? formRequest<Connection>(`/workspaces/${workspaceId}/connections`, 'POST', connectionForm(name, settings, file, connector))
    : request<Connection>(`/workspaces/${workspaceId}/connections`, 'POST', { name, connector, settings })

/** Replaces the file a connection reads, which queues a run of each of its syncs. */
export function replaceConnectionFile(id: number, file: File) {
  const form = new FormData()
  form.append('file', file)
  return formRequest<Connection>(`/connections/${id}/file`, 'PUT', form)
}
export const removeConnection = (id: number) => request<null>(`/connections/${id}`, 'DELETE')
export const listStreams = (connectionId: number) => request<Stream[]>(`/connections/${connectionId}/streams`)

export const listSyncs = (workspaceId: number) => request<Sync[]>(`/workspaces/${workspaceId}/syncs`)
export const addSyncs = (connectionId: number, destinationId: number, streams: SyncPick[]) =>
  request<Sync[]>(`/connections/${connectionId}/syncs`, 'POST', { destinationId, streams })
export const editSync = (id: number, edit: SyncEdit) => request<Sync>(`/syncs/${id}`, 'PATCH', edit)
export const runSync = (id: number) => request<Sync>(`/syncs/${id}/run`, 'POST')
export const listRuns = (id: number) => request<Run[]>(`/syncs/${id}/runs`)
export const removeSync = (id: number) => request<null>(`/syncs/${id}`, 'DELETE')

export interface ConnectView {
  appName: string
  workspaceName: string
  connector: Connector
  destinationUrl: string
  expiresAt: string
  connection: Connection | null
}

export interface Pairing {
  id: string
  appName: string
  ingestUrl: string
  connectReturnUrl: string
  expiresAt: string
}

export const getSetup = () => request<{ needed: boolean }>('/setup')
export const setUp = (code: string, email: string, password: string) => request<Me>('/setup', 'POST', { code, email, password })
export const getPairing = (id: string) => request<Pairing>(`/pairings/${encodeURIComponent(id)}`)
export const approvePairing = (id: string, workspaceId: number) =>
  request<{ returnUrl: string }>(`/pairings/${encodeURIComponent(id)}/approve`, 'POST', { workspaceId })
export const declinePairing = (id: string) => request<{ returnUrl: string }>(`/pairings/${encodeURIComponent(id)}/decline`, 'POST')

export const getConnect = (token: string) => connectRequest<ConnectView>(token, '/connect')
export const connectConnection = (token: string, name: string, settings: Record<string, string>, file?: File) =>
  file
    ? connectForm<ConnectView>(token, '/connect/connection', connectionForm(name, settings, file))
    : connectRequest<ConnectView>(token, '/connect/connection', 'POST', { name, settings })
export const connectStreams = (token: string) => connectRequest<Stream[]>(token, '/connect/streams')
export const finishConnect = (token: string, streams: SyncPick[]) =>
  connectRequest<{ returnUrl: string }>(token, '/connect/syncs', 'POST', { streams })
export const cancelConnect = (token: string) => connectRequest<{ returnUrl: string }>(token, '/connect/cancel', 'POST')

/** A stream's name as a table name a destination such as Scry takes: lower case, with underscores for the rest. */
export function tableName(stream: string): string {
  const name = stream.toLowerCase().replace(/[^a-z0-9_]+/g, '_').replace(/^_+|_+$/g, '')
  return (/^[a-z]/.test(name) ? name : `t_${name}`).slice(0, 63)
}
