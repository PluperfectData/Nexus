import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { getLog, listWorkspaces, type LogEntries, type LogEntry, type Me } from '../api'
import { SessionContext } from '../session'
import { LogPage } from './LogPage'

jest.mock('react-router', () => jest.requireActual('../../jest/router').reactRouterMock())
const { routerState, setSearchParams } = jest.requireActual('../../jest/router')

jest.mock('../api', () => ({ ...jest.requireActual('../api'), getLog: jest.fn(), listWorkspaces: jest.fn() }))

const admin: Me = { signedIn: true, id: 1, email: 'admin@acme.test', isAdmin: true, hasPassword: true }
const member: Me = { ...admin, id: 2, email: 'member@acme.test', isAdmin: false }

const ran: LogEntry = {
  id: 12,
  at: '2026-11-16T15:00:00Z',
  level: 'Information',
  text: 'Sync orders sent 1,204 rows from Shop (MySQL), stream shop.orders, into Scry at http://localhost:6161/ingest.',
  actor: null,
  workspaceId: 4,
  workspaceName: 'Acme',
  connectionId: 9,
  syncId: 11,
}

const failed: LogEntry = {
  ...ran,
  id: 11,
  level: 'Error',
  text: 'Sync orders failed: The server refused the password.',
}

const invited: LogEntry = {
  ...ran,
  id: 10,
  text: 'bob@acme.test was invited.',
  actor: 'admin@acme.test',
  workspaceId: null,
  workspaceName: null,
  connectionId: null,
  syncId: null,
}

const page = (entries: LogEntry[], next: number | null = null, subject: string | null = null): LogEntries => ({ entries, next, subject })

const renderAs = (me: Me) =>
  render(
    <SessionContext.Provider value={{ me, setMe: jest.fn() }}>
      <LogPage />
    </SessionContext.Provider>,
  )

beforeEach(() => {
  jest.clearAllMocks()
  routerState.search = new URLSearchParams()
  jest.mocked(listWorkspaces).mockResolvedValue([{ id: 4, name: 'Acme', createdAt: '2026-11-15T12:00:00Z' }])
  jest.mocked(getLog).mockResolvedValue(page([ran, failed, invited]))
})

it('lists what happened, newest first, with each entry level in words, its workspace, and who did it', async () => {
  renderAs(admin)

  const entries = within(await screen.findByRole('list')).getAllByRole('listitem')
  expect(entries).toHaveLength(3)
  expect(within(entries[0]).getByText(ran.text)).toBeInTheDocument()
  expect(within(entries[0]).getByText('Info')).toBeInTheDocument()
  expect(within(entries[0]).getByRole('link', { name: 'Acme' })).toHaveAttribute('href', '/workspaces/4')
  expect(within(entries[1]).getByText('Error')).toBeInTheDocument()
  expect(within(entries[2]).getByText('The instance')).toBeInTheDocument()
  expect(within(entries[2]).getByText('by admin@acme.test')).toBeInTheDocument()
  expect(getLog).toHaveBeenCalledWith({})
})

it('filters by workspace and by level through the address', async () => {
  renderAs(admin)
  await screen.findByText(ran.text)

  await userEvent.selectOptions(screen.getByLabelText('Show'), 'Warnings and errors')
  expect(String(setSearchParams.mock.lastCall[0])).toBe('level=Warning')

  await userEvent.selectOptions(screen.getByLabelText('Workspace'), 'Acme')
  expect(String(setSearchParams.mock.lastCall[0])).toBe('workspace=4')
})

it('reads the filter from the address it was opened at', async () => {
  routerState.search = new URLSearchParams('workspace=instance&level=Warning')
  renderAs(admin)
  await screen.findByText(ran.text)

  expect(getLog).toHaveBeenCalledWith({ instance: true, level: 'Warning' })
})

it("names the sync it shows, and goes back to every entry", async () => {
  routerState.search = new URLSearchParams('sync=11')
  jest.mocked(getLog).mockResolvedValue(page([ran], null, 'Sync orders'))
  renderAs(member)

  expect(await screen.findByText(/Showing Sync orders only/)).toBeInTheDocument()
  expect(getLog).toHaveBeenCalledWith({ sync: 11 })

  await userEvent.click(screen.getByRole('button', { name: 'Show every entry' }))
  expect(String(setSearchParams.mock.lastCall[0])).toBe('')
})

it('shows older entries below the ones it has', async () => {
  jest.mocked(getLog).mockResolvedValueOnce(page([ran], 12)).mockResolvedValueOnce(page([invited]))
  renderAs(admin)
  await screen.findByText(ran.text)

  await userEvent.click(screen.getByRole('button', { name: 'Show older' }))

  expect(getLog).toHaveBeenLastCalledWith({ before: 12 })
  expect(await screen.findByText(invited.text)).toBeInTheDocument()
  expect(screen.getByText(ran.text)).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Show older' })).not.toBeInTheDocument()
})

it("offers a member their own workspaces, and not the instance's entries", async () => {
  renderAs(member)
  await screen.findByText(ran.text)

  const workspace = screen.getByLabelText('Workspace')
  expect(within(workspace).getByRole('option', { name: 'All my workspaces' })).toBeInTheDocument()
  expect(within(workspace).queryByRole('option', { name: /The instance/ })).not.toBeInTheDocument()
  expect(screen.getByText('What happened in your workspaces, newest first.')).toBeInTheDocument()
})

it('says when nothing has been logged', async () => {
  jest.mocked(getLog).mockResolvedValue(page([]))
  renderAs(admin)

  expect(await screen.findByText('Nothing has been logged here yet.')).toBeInTheDocument()
})

it("shows the service's reason when it refuses", async () => {
  jest.mocked(getLog).mockRejectedValue(new Error('Not found.'))
  renderAs(member)

  expect(await screen.findByText('Not found.')).toBeInTheDocument()
})
