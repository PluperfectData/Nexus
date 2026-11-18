import type { Sync } from './api'
import { targetOf } from './places'

/**
 * A connection's tables in a line, for its card while they are hidden: how many, and how many failed, are queued or
 * running, or need a destination. `problem` is whether any failed or needs a destination.
 */
export function tablesSummary(syncs: Sync[]): { text: string; problem: boolean } {
  if (syncs.length === 0) return { text: 'No tables yet', problem: false }
  const homeless = syncs.filter((s) => targetOf(s) === null).length
  // A table without a destination fails for that, so it is counted there alone.
  const failed = syncs.filter((s) => s.status === 'Failed' && targetOf(s) !== null).length
  const queued = syncs.filter((s) => s.status === 'Queued').length
  const running = syncs.filter((s) => s.status === 'Running').length
  const parts = [
    `${syncs.length} ${syncs.length === 1 ? 'table' : 'tables'}`,
    ...(failed > 0 ? [`${failed} failed`] : []),
    ...(queued > 0 ? [`${queued} queued`] : []),
    ...(running > 0 ? [`${running} running`] : []),
    ...(homeless > 0 ? [`${homeless} ${homeless === 1 ? 'needs' : 'need'} a destination`] : []),
  ]
  return { text: parts.join(', '), problem: failed > 0 || homeless > 0 }
}
