import { createBrowserRouter } from 'react-router'
import { Layout, RequireSession } from './components/Layout'
import { AccountPage } from './pages/AccountPage'
import { ConnectPage } from './pages/ConnectPage'
import { InstallPage } from './pages/InstallPage'
import { LinkPage } from './pages/LinkPage'
import { LogPage } from './pages/LogPage'
import { PairPage } from './pages/PairPage'
import { SettingsPage } from './pages/SettingsPage'
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
      { path: 'install', element: <InstallPage /> },
      {
        element: <RequireSession />,
        children: [
          { index: true, element: <WorkspacesPage /> },
          { path: 'workspaces/:id', element: <WorkspacePage /> },
          { path: 'users', element: <UsersPage /> },
          { path: 'settings', element: <SettingsPage /> },
          { path: 'log', element: <LogPage /> },
          { path: 'account', element: <AccountPage /> },
          { path: 'pair', element: <PairPage /> },
        ],
      },
    ],
  },
])
