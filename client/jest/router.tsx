// A stand-in for react-router in unit tests: Jest cannot load its ES-module-only build.
import type { ReactNode } from 'react'

export const navigate = jest.fn()
export const routerState = { params: {} as Record<string, string>, pathname: '/' }

type LinkProps = { to: string; children: ReactNode; className?: string | ((state: { isActive: boolean }) => string); role?: string }

const link = ({ to, children, className, role }: LinkProps) => (
  <a href={to} role={role} className={typeof className === 'function' ? className({ isActive: routerState.pathname === to }) : className}>
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
})
