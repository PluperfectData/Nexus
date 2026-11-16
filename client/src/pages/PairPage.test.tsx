import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { approvePairing, declinePairing, getPairing, listWorkspaces, type Me } from '../api'
import { leaveFor } from '../browser'
import { SessionContext } from '../session'
import { PairPage } from './PairPage'

jest.mock('react-router', () => jest.requireActual('../../jest/router').reactRouterMock())

jest.mock('../api', () => ({
  ...jest.requireActual('../api'),
  getPairing: jest.fn(),
  listWorkspaces: jest.fn(),
  approvePairing: jest.fn(),
  declinePairing: jest.fn(),
}))

jest.mock('../browser', () => ({ leaveFor: jest.fn() }))

const admin: Me = { signedIn: true, id: 1, email: 'admin@acme.test', isAdmin: true, hasPassword: true }

const renderAs = (me: Me) =>
  render(
    <SessionContext.Provider value={{ me, setMe: jest.fn() }}>
      <PairPage />
    </SessionContext.Provider>,
  )

beforeEach(() => {
  jest.clearAllMocks()
  window.history.replaceState(null, '', '/pair?id=p-123')
  jest.mocked(getPairing).mockResolvedValue({
    id: 'p-123',
    appName: 'Scry',
    ingestUrl: 'http://localhost:6161/ingest',
    connectReturnUrl: 'http://localhost:6160/sources',
    expiresAt: '2026-11-15T12:10:00Z',
  })
  jest.mocked(listWorkspaces).mockResolvedValue([
    { id: 4, name: 'Acme', createdAt: '2026-11-15T12:00:00Z' },
    { id: 5, name: 'Globex', createdAt: '2026-11-15T12:00:00Z' },
  ])
})

it('approves the app into the workspace picked, and goes back to the app', async () => {
  jest.mocked(approvePairing).mockResolvedValue({ returnUrl: 'http://localhost:6161/nexus/callback?pairing=p-123' })
  renderAs(admin)

  expect(await screen.findByRole('heading', { name: 'Link Scry' })).toBeInTheDocument()
  expect(screen.getByText(/at localhost:6161 asks to link/)).toBeInTheDocument()
  await userEvent.selectOptions(screen.getByLabelText('Workspace'), 'Globex')
  await userEvent.click(screen.getByRole('button', { name: 'Approve and go back to Scry' }))

  expect(approvePairing).toHaveBeenCalledWith('p-123', 5)
  expect(leaveFor).toHaveBeenCalledWith('http://localhost:6161/nexus/callback?pairing=p-123')
})

it('declines, and goes back to the app', async () => {
  jest.mocked(declinePairing).mockResolvedValue({ returnUrl: 'http://localhost:6161/nexus/callback?error=declined' })
  renderAs(admin)

  await userEvent.click(await screen.findByRole('button', { name: 'Decline' }))

  expect(leaveFor).toHaveBeenCalledWith('http://localhost:6161/nexus/callback?error=declined')
})

it('tells a member that only an admin links an app', () => {
  renderAs({ ...admin, isAdmin: false })

  expect(screen.getByText('Only an admin of this instance can link an app. Ask one to open this page.')).toBeInTheDocument()
  expect(getPairing).not.toHaveBeenCalled()
})
