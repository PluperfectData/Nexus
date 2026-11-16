import { createBrowserRouter } from 'react-router'
import { Layout, RequireSession } from './components/Layout'
import { AccountPage } from './pages/AccountPage'
import { ConnectPage } from './pages/ConnectPage'
import { LinkPage } from './pages/LinkPage'
import { PairPage } from './pages/PairPage'
import { SetupPage } from './pages/SetupPage'
import { SignInPage } from './pages/SignInPage'
import { UsersPage } from './pages/UsersPage'
import { WorkspacePage } from './pages/WorkspacePage'
import { WorkspacesPage } from './pages/WorkspacesPage'

export const router = createBrowserRouter([
  {
    element: <Layout />,
    children: [
      { path: 'sign-in', element: <SignInPage /> },
      { path: 'sign-in/link', element: <LinkPage /> },
      { path: 'connect', element: <ConnectPage /> },
      { path: 'setup', element: <SetupPage /> },
      {
        element: <RequireSession />,
        children: [
          { index: true, element: <WorkspacesPage /> },
          { path: 'workspaces/:id', element: <WorkspacePage /> },
          { path: 'users', element: <UsersPage /> },
          { path: 'account', element: <AccountPage /> },
          { path: 'pair', element: <PairPage /> },
        ],
      },
    ],
  },
])
