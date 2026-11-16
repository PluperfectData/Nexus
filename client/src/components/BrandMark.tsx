// White, blue, plum, red, and green, in that order around the core.
const STREAMS = ['#f5eed6', '#3a86d6', '#82378a', '#e2483d', '#33a35f']

/** Nexus's mark: five streams curling into one gray core, on its own dark tile so its colors hold in either theme. */
export function BrandMark() {
  return (
    <svg className="brand-mark" viewBox="-60 -60 120 120" aria-hidden="true" focusable="false">
      <rect x="-60" y="-60" width="120" height="120" rx="24" fill="#1a1f3d" />
      <g fill="none" strokeWidth="8" strokeLinecap="round">
        {STREAMS.map((color, i) => (
          <g key={color} transform={`rotate(${i * 72})`}>
            <path d="M0 -42 C22 -42 32 -20 10 -7" stroke={color} />
            <circle cx="0" cy="-42" r="6" fill={color} />
          </g>
        ))}
      </g>
      <circle r="8" fill="#8b919c" />
    </svg>
  )
}
