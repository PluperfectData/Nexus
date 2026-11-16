/** Leaves the client for another site's address. A module of its own, so tests can stand in for it. */
export function leaveFor(url: string) {
  window.location.assign(url)
}
