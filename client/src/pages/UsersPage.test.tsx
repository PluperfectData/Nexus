import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { editUser, inviteUser, listUsers, type Me, type User } from '../api'
import { SessionContext } from '../session'
import { UsersPage } from './UsersPage'

jest.mock('../api', () => ({ ...jest.requireActual('../api'), listUsers: jest.fn(), inviteUser: jest.fn(), editUser: jest.fn() }))

const admin: Me = { signedIn: true, id: 1, email: 'admin@acme.test', isAdmin: true, hasPassword: true }

const user = (id: number, email: string, isAdmin = false): User => ({
  id,
  email,
  isAdmin,
  hasPassword: false,
  createdAt: '2026-11-15T12:00:00Z',
  signedInAt: null,
  workspaces: [],
})

const renderAs = (me: Me) =>
  render(
    <SessionContext.Provider value={{ me, setMe: jest.fn() }}>
      <UsersPage />
    </SessionContext.Provider>,
  )

beforeEach(() => {
  jest.clearAllMocks()
  jest.mocked(listUsers).mockResolvedValue([user(1, 'admin@acme.test', true), user(2, 'rowan@acme.test')])
})

it('invites a user by mail', async () => {
  jest.mocked(inviteUser).mockResolvedValue(user(3, 'new@acme.test', true))
  renderAs(admin)
  await screen.findByText('rowan@acme.test')

  await userEvent.type(screen.getByLabelText('Invite by email'), 'new@acme.test')
  await userEvent.click(screen.getByLabelText('As an admin'))
  await userEvent.click(screen.getByRole('button', { name: 'Invite' }))

  expect(inviteUser).toHaveBeenCalledWith('new@acme.test', true)
  expect(await screen.findByText('Invited new@acme.test. The invite link works for 7 days.')).toBeInTheDocument()
  expect(screen.getByText('new@acme.test')).toBeInTheDocument()
})

it('makes a member an admin, and offers no role change or removal for the admin asking', async () => {
  jest.mocked(editUser).mockResolvedValue(user(2, 'rowan@acme.test', true))
  renderAs(admin)
  const rowan = (await screen.findByText('rowan@acme.test')).closest('li')!
  const self = screen.getByText('admin@acme.test').closest('li')!

  expect(within(self).queryByRole('button', { name: 'Remove' })).not.toBeInTheDocument()
  await userEvent.click(within(rowan).getByRole('button', { name: 'Make admin' }))

  expect(editUser).toHaveBeenCalledWith(2, true)
  expect(await within(rowan).findByRole('button', { name: 'Make member' })).toBeInTheDocument()
})

it('tells a member that only admins manage users', () => {
  renderAs({ ...admin, isAdmin: false })

  expect(screen.getByText('Only admins manage users.')).toBeInTheDocument()
  expect(listUsers).not.toHaveBeenCalled()
})
