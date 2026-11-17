import { render, screen, waitFor } from '@testing-library/react'
import { type ReactNode, useState } from 'react'
import userEvent from '@testing-library/user-event'
import { ApiError, getInstall, type InstallView, mailLink, signIn, type Me } from '../api'
import { rememberNext } from '../next'
import { SessionContext, SIGNED_OUT } from '../session'
import { SignInPage } from './SignInPage'

jest.mock('react-router', () => jest.requireActual('../../jest/router').reactRouterMock())
const { navigate } = jest.requireActual('../../jest/router')

jest.mock('../api', () => ({ ...jest.requireActual('../api'), signIn: jest.fn(), mailLink: jest.fn(), getInstall: jest.fn() }))

const rowan: Me = { signedIn: true, id: 1, email: 'rowan@acme.test', isAdmin: false, hasPassword: true }

const installed: InstallView = {
  installed: true,
  stage: 'Done',
  local: false,
  allowed: false,
  databaseConfigured: false,
  database: null,
  databaseProblem: null,
  clientOriginConfigured: false,
  mail: true,
  mailSettings: null,
}

// A session that changes when the page sets it, as the app's does, so the page re-renders signed in.
function Session({ onSet, children }: { onSet: (me: Me) => void; children: ReactNode }) {
  const [me, setMe] = useState<Me>(SIGNED_OUT)
  const set = (next: Me) => {
    onSet(next)
    setMe(next)
  }
  return <SessionContext.Provider value={{ me, setMe: set }}>{children}</SessionContext.Provider>
}

function renderPage(setMe = jest.fn()) {
  render(
    <Session onSet={setMe}>
      <SignInPage />
    </Session>,
  )
  return setMe
}

beforeEach(() => {
  jest.clearAllMocks()
  localStorage.clear()
  jest.mocked(getInstall).mockResolvedValue(installed)
})

it('sends the first visitor of an instance not set up to the installer', async () => {
  jest.mocked(getInstall).mockResolvedValue({ ...installed, installed: false, stage: 'Database' })
  renderPage()

  await screen.findByRole('heading', { name: 'Sign in to Nexus' })
  await new Promise((resolve) => setTimeout(resolve, 0))

  expect(navigate).toHaveBeenCalledWith('/install', { replace: true })
})

it('offers no mailed link on an instance without mail', async () => {
  jest.mocked(getInstall).mockResolvedValue({ ...installed, mail: false })
  renderPage()

  await waitFor(() => expect(getInstall).toHaveBeenCalled())
  await screen.findByLabelText('Password')

  expect(screen.queryByRole('button', { name: 'Mail me a sign-in link instead' })).not.toBeInTheDocument()
})

it('goes back to the page that sent the visitor to sign in', async () => {
  rememberNext('/pair?id=p-123')
  jest.mocked(signIn).mockResolvedValue(rowan)
  renderPage()

  await userEvent.type(screen.getByLabelText('Email'), 'rowan@acme.test')
  await userEvent.type(screen.getByLabelText('Password'), 'correct horse battery')
  await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

  expect(navigate).toHaveBeenCalledWith('/pair?id=p-123')
  expect(navigate).not.toHaveBeenCalledWith('/')
})

it('lands on the workspaces when the page it remembered is stale', async () => {
  localStorage.setItem('nexus-next', JSON.stringify({ path: '/pair?id=p-123', at: Date.now() - 11 * 60 * 1000 }))
  jest.mocked(signIn).mockResolvedValue(rowan)
  renderPage()

  await userEvent.type(screen.getByLabelText('Email'), 'rowan@acme.test')
  await userEvent.type(screen.getByLabelText('Password'), 'correct horse battery')
  await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

  expect(navigate).toHaveBeenCalledWith('/')
})

it('signs in with a password and opens the workspaces', async () => {
  jest.mocked(signIn).mockResolvedValue(rowan)
  const setMe = renderPage()

  await userEvent.type(screen.getByLabelText('Email'), 'rowan@acme.test')
  await userEvent.type(screen.getByLabelText('Password'), 'correct horse battery')
  await userEvent.click(screen.getByLabelText('Keep me signed in on this device'))
  await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

  expect(signIn).toHaveBeenCalledWith('rowan@acme.test', 'correct horse battery', true)
  expect(setMe).toHaveBeenCalledWith(rowan)
  expect(navigate).toHaveBeenCalledWith('/')
})

it('says why a sign-in was refused', async () => {
  jest.mocked(signIn).mockRejectedValue(new ApiError(401, 'That email and password do not match.'))
  renderPage()

  await userEvent.type(screen.getByLabelText('Email'), 'rowan@acme.test')
  await userEvent.type(screen.getByLabelText('Password'), 'wrong')
  await userEvent.click(screen.getByRole('button', { name: 'Sign in' }))

  expect(await screen.findByText('That email and password do not match.')).toBeInTheDocument()
})

it('mails a link instead, and says one is on its way', async () => {
  jest.mocked(mailLink).mockResolvedValue(null)
  renderPage()

  await userEvent.click(await screen.findByRole('button', { name: 'Mail me a sign-in link instead' }))
  expect(screen.queryByLabelText('Password')).not.toBeInTheDocument()
  await userEvent.type(screen.getByLabelText('Email'), 'rowan@acme.test')
  await userEvent.click(screen.getByRole('button', { name: 'Mail me a link' }))

  expect(mailLink).toHaveBeenCalledWith('rowan@acme.test')
  expect(await screen.findByText(/a sign-in link is on its way/)).toBeInTheDocument()
})
