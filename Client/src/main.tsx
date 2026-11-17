import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { RouterProvider } from 'react-router'
import './index.css'
import { router } from './router'
import { SessionProvider } from './SessionProvider'
import { ThemeProvider } from './ThemeProvider'
import { WorkspacesProvider } from './components/WorkspacesProvider'

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <ThemeProvider>
      <SessionProvider>
        <WorkspacesProvider>
          <RouterProvider router={router} />
        </WorkspacesProvider>
      </SessionProvider>
    </ThemeProvider>
  </StrictMode>,
)
