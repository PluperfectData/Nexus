import type { EntryLevel } from '../api'

/** A level's shape, so it is told apart by more than its color. */
export function LevelIcon({ level }: { level: EntryLevel }) {
  const shape =
    level === 'Error' ? (
      <path d="M12 3l9 9-9 9-9-9z M12 8v5 M12 16v.01" />
    ) : level === 'Warning' ? (
      <path d="M12 4l9 16H3z M12 10v4 M12 17v.01" />
    ) : (
      <path d="M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18z M12 11v6 M12 7.5v.01" />
    )
  return (
    <svg viewBox="0 0 24 24" aria-hidden="true" focusable="false" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round">
      {shape}
    </svg>
  )
}
