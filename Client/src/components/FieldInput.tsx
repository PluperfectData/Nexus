import type { Field } from '../api'

/**
 * One setting a connector asks for: a checkbox, a pick from its choices, a file, or text, masked when it is a secret.
 * A file goes to `onFile`, and every other value to `onChange`.
 */
export function FieldInput({
  field,
  value,
  onChange,
  onFile,
}: {
  field: Field
  value: string
  onChange: (value: string) => void
  onFile?: (file: File | null) => void
}) {
  if (field.kind === 'Boolean') {
    return (
      <label className="check">
        <input type="checkbox" checked={value === 'true'} onChange={(e) => onChange(e.target.checked ? 'true' : 'false')} />
        {field.label}
      </label>
    )
  }

  // The service refuses a missing file in its own words, so the input is not marked required.
  if (field.kind === 'File') {
    return (
      <label>
        {field.label}
        <input type="file" onChange={(e) => onFile?.(e.target.files?.[0] ?? null)} />
      </label>
    )
  }

  return (
    <label>
      {field.label}
      {field.kind === 'Choice' ? (
        <select value={value} onChange={(e) => onChange(e.target.value)} required={field.required}>
          <option value="">Pick one</option>
          {(field.choices ?? []).map((choice) => (
            <option key={choice}>{choice}</option>
          ))}
        </select>
      ) : (
        <input
          type={field.kind === 'Secret' ? 'password' : field.kind === 'Number' ? 'number' : 'text'}
          value={value}
          onChange={(e) => onChange(e.target.value)}
          required={field.required}
          autoComplete="off"
        />
      )}
    </label>
  )
}
