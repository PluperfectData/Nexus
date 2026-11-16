import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { listWorkspaces, makeWorkspace, type Me } from '../api'
import { SessionContext } from '../session'
import { WorkspacesPage } from './WorkspacesPage'

jest.mock('react-router', () => jest.requireActual('../../jest/router').reactRouterMock())

jest.mock('../api', () => ({ ...jest.requireActual('../api'), listWorkspaces: jest.fn(), makeWorkspace: jest.fn() }))

const member: Me = { signedIn: true, id: 2, email: 'member@acme.test', isAdmin: false, hasPassword: true }

const renderAs = (me: Me) =>
  render(
    <SessionContext.Provider value={{ me, setMe: jest.fn() }}>
      <WorkspacesPage />
    </SessionContext.Provider>,
  )

beforeEach(() => jest.clearAllMocks())

it('links each workspace the user may see, and offers a member no form', async () => {
  jest.mocked(listWorkspaces).mockResolvedValue([{ id: 4, name: 'Acme', createdAt: '2026-11-15T12:00:00Z' }])
  renderAs(member)

  expect(await screen.findByRole('link', { name: 'Acme' })).toHaveAttribute('href', '/workspaces/4')
  expect(screen.queryByLabelText('New workspace')).not.toBeInTheDocument()
})

it('lets an admin make a workspace', async () => {
  jest.mocked(listWorkspaces).mockResolvedValue([])
  jest.mocked(makeWorkspace).mockResolvedValue({ id: 5, name: 'Globex', createdAt: '2026-11-15T12:00:00Z', members: [] })
  renderAs({ ...member, isAdmin: true })
  await screen.findByText('No workspaces yet. Make the first one below.')

  await userEvent.type(screen.getByLabelText('New workspace'), 'Globex')
  await userEvent.click(screen.getByRole('button', { name: 'Make workspace' }))

  expect(makeWorkspace).toHaveBeenCalledWith('Globex')
  expect(await screen.findByRole('link', { name: 'Globex' })).toBeInTheDocument()
})
