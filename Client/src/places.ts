import type { App, Destination, Sync } from './api'

/** A destination with the name of the app it belongs to, as a sync's pickers list it. */
export type Place = Destination & { appName: string }

export const placesOf = (apps: App[]): Place[] => apps.flatMap((app) => app.destinations.map((d) => ({ ...d, appName: app.name })))

/** The push key a destination sends, as its push API named it at the last check. */
export function keyLabel(prefix: string | null, name: string | null): string {
  if (!prefix) return 'key not checked yet'
  return name ? `key ${prefix}…, named ${name}` : `key ${prefix}…`
}

export const placeLabel = (place: Place) =>
  `${place.appName} at ${place.url} (destination ${place.id}, added ${new Date(place.createdAt).toLocaleDateString()}, ${keyLabel(place.keyPrefix, place.keyName)}${place.keyProblem ? ', key refused' : ''})`

/** Where a sync writes, or null once its destination was removed. */
export const targetOf = (sync: Sync): string | null =>
  sync.destinationId === null
    ? null
    : `${sync.appName} at ${sync.destinationUrl} (destination ${sync.destinationId}, ${keyLabel(sync.destinationKeyPrefix, sync.destinationKeyName)})`
