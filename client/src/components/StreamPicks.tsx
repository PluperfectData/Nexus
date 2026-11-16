import type { Stream, SyncMode } from '../api'
import { MODES, type Pick } from '../streams'

/** A checkbox for each stream, and for each one picked, its table name and how it is read. */
export function StreamPicks({
  streams,
  picks,
  onPick,
}: {
  streams: Stream[]
  picks: Record<string, Pick>
  onPick: (stream: string, change: Partial<Pick>) => void
}) {
  return (
    <fieldset className="wide streams">
      <legend>Streams</legend>
      {streams.length === 0 && <p className="muted">This connection offers no streams.</p>}
      {streams.map((stream) => {
        const p = picks[stream.name]
        return (
          <div key={stream.name} className="stream-pick">
            <label className="check">
              <input type="checkbox" checked={p.on} onChange={(e) => onPick(stream.name, { on: e.target.checked })} />
              {stream.name}
            </label>
            {p.on && (
              <>
                <label>
                  Table
                  <input value={p.table} onChange={(e) => onPick(stream.name, { table: e.target.value })} required maxLength={63} />
                </label>
                <label>
                  Read
                  <select value={p.mode} onChange={(e) => onPick(stream.name, { mode: e.target.value as SyncMode })}>
                    {stream.syncModes.map((mode) => (
                      <option key={mode} value={mode}>
                        {MODES[mode]}
                      </option>
                    ))}
                  </select>
                </label>
              </>
            )}
          </div>
        )
      })}
    </fieldset>
  )
}
