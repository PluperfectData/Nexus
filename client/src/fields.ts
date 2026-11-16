import type { Field } from './api'

/** The values a connector's form starts from: each field's default, where it has one. */
export const defaultsOf = (fields: Field[]): Record<string, string> =>
  Object.fromEntries(fields.filter((f) => f.kind !== 'File' && f.default !== null).map((f) => [f.name, f.default as string]))
