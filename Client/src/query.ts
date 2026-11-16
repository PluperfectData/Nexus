/** The values the page's address came back with, such as `error` from a sign-in. Reading them changes nothing. */
export const arrivalQuery = () => new URLSearchParams(window.location.search)

/** Clears the address's query once a page has read it, keeping any `#` part, such as a connect page's token. */
export function clearQuery() {
  if (window.location.search) {
    window.history.replaceState(window.history.state, '', window.location.pathname + window.location.hash)
  }
}
