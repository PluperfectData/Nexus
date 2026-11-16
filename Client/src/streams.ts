import { type Stream, type SyncMode, tableName } from './api'

export const MODES: Record<SyncMode, string> = { FullRefresh: 'Full refresh', Incremental: 'Incremental' }

/** Whether a stream is picked, and if so, its table and how it is read. */
export interface Pick {
  on: boolean
  table: string
  mode: SyncMode
}

/** Every stream unpicked, each with a suggested table name and its first mode. */
export const initialPicks = (streams: Stream[]): Record<string, Pick> =>
  Object.fromEntries(streams.map((s) => [s.name, { on: false, table: tableName(s.name), mode: s.syncModes[0] ?? 'FullRefresh' }]))

/** The picked streams, as the service takes them. */
export const picked = (picks: Record<string, Pick>) =>
  Object.entries(picks)
    .filter(([, p]) => p.on)
    .map(([stream, p]) => ({ stream, table: p.table, mode: p.mode }))
