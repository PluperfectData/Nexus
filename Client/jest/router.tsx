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
}

const link = ({ to, children, className, role, 'aria-label': label }: LinkProps) => (
  <a
    href={to}
    role={role}
    aria-label={label}
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
  useLocation: () => ({ pathname: routerState.pathname, hash: '', state: null }),
  useSearchParams: () => [routerState.search, setSearchParams],
})
