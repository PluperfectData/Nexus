// A stand-in for react-router in unit tests: Jest cannot load its ES-module-only build.
import type { ReactNode } from 'react'

export const navigate = jest.fn()
export const routerState = { params: {} as Record<string, string>, pathname: '/', search: new URLSearchParams() }

/** Keeps the address's query, as the page asked, for the test to render again with. */
export const setSearchParams = jest.fn((next: URLSearchParams) => {
  routerState.search = next
})

type LinkProps = {
  to: string
  children: ReactNode
  className?: string | ((state: { isActive: boolean }) => string)
  role?: string
  'aria-label'?: string
  'aria-current'?: 'page'
}

const link = ({ to, children, className, role, 'aria-label': label, 'aria-current': current }: LinkProps) => (
  <a
    href={to}
    role={role}
    aria-label={label}
    aria-current={current}
    className={typeof className === 'function' ? className({ isActive: routerState.pathname === to }) : className}
  >
    {children}
  </a>
)

export const reactRouterMock = () => ({
  Link: link,
  NavLink: link,
  Outlet: () => null,
  Navigate: ({ to }: { to: string }) => {
    navigate(to)
    return null
  },
  useNavigate: () => navigate,
  useParams: () => routerState.params,
  useMatch: (pattern: string) => {
    const names: string[] = []
    const source = pattern.replace(/\/\*$/, '(?:/.*)?').replace(/:(\w+)/g, (_: string, name: string) => {
      names.push(name)
      return '([^/]+)'
    })
    const found = new RegExp(`^${source}$`).exec(routerState.pathname)
    return found ? { params: Object.fromEntries(names.map((name, i) => [name, found[i + 1]])) } : null
  },
  useLocation: () => ({ pathname: routerState.pathname, hash: '', state: null }),
  useSearchParams: () => [routerState.search, setSearchParams],
})
