import type { Field } from '../api'

/** One setting a connector asks for: a checkbox, a pick from its choices, or text, masked when it is a secret. */
export function FieldInput({ field, value, onChange }: { field: Field; value: string; onChange: (value: string) => void }) {
  if (field.kind === 'Boolean') {
    return (
      <label className="check">
        <input type="checkbox" checked={value === 'true'} onChange={(e) => onChange(e.target.checked ? 'true' : '')} />
        {field.label}
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
