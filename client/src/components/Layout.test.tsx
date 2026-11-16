import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { Me } from '../api'
import { peekNext, rememberNext } from '../next'
import { SessionContext, SIGNED_OUT } from '../session'
import { Layout, RequireSession } from './Layout'

jest.mock('react-router', () => jest.requireActual('../../jest/router').reactRouterMock())
const { navigate, routerState } = jest.requireActual('../../jest/router')

jest.mock('../api', () => ({ ...jest.requireActual('../api'), signOut: jest.fn().mockResolvedValue(null) }))

const member: Me = { signedIn: true, id: 2, email: 'member@acme.test', isAdmin: false, hasPassword: true }

const renderWith = (me: Me | null, element = <Layout />, setMe = jest.fn()) =>
  render(<SessionContext.Provider value={{ me, setMe }}>{element}</SessionContext.Provider>)

beforeEach(() => jest.clearAllMocks())

it('shows Users in the nav to an admin only, and keeps the account out of the nav', () => {
  const { unmount } = renderWith(member)
  const nav = screen.getByRole('navigation', { name: 'Main' })
  expect(within(nav).queryByRole('link', { name: 'Users' })).not.toBeInTheDocument()
  expect(within(nav).queryByRole('link', { name: 'Account' })).not.toBeInTheDocument()
  unmount()

  renderWith({ ...member, isAdmin: true })
  expect(within(screen.getByRole('navigation', { name: 'Main' })).getByRole('link', { name: 'Users' })).toBeInTheDocument()
})

it('opens the nav from the menu button, which says what a press does', async () => {
  renderWith(member)
  const toggle = screen.getByRole('button', { name: 'Open menu' })

  await userEvent.click(toggle)

  expect(toggle).toHaveAttribute('aria-expanded', 'true')
  expect(screen.getByRole('navigation', { name: 'Main' })).toHaveAttribute('data-open')
  expect(screen.getByRole('button', { name: 'Close menu' })).toBeInTheDocument()
})

it('names the user and their role on the account button, and opens a menu with the account page', async () => {
  renderWith({ ...member, isAdmin: true })
  const button = screen.getByRole('button', { name: 'Account menu for member@acme.test, Admin' })
  expect(button).toHaveAttribute('title', 'member@acme.test, Admin')

  await userEvent.click(button)

  const menu = screen.getByRole('menu', { name: 'Account' })
  expect(button).toHaveAttribute('aria-expanded', 'true')
  expect(within(menu).getByText('member@acme.test')).toBeInTheDocument()
  expect(within(menu).getByText('Admin')).toBeInTheDocument()
  expect(within(menu).getByRole('menuitem', { name: 'Account' })).toHaveAttribute('href', '/account')

  await userEvent.keyboard('{Escape}')
  expect(screen.queryByRole('menu')).not.toBeInTheDocument()
})

it('signs out from the account menu and goes to the sign-in page', async () => {
  const setMe = jest.fn()
  renderWith(member, <Layout />, setMe)

  await userEvent.click(screen.getByRole('button', { name: 'Account menu for member@acme.test, Member' }))
  await userEvent.click(screen.getByRole('menuitem', { name: 'Sign out' }))

  expect(setMe).toHaveBeenCalledWith(SIGNED_OUT)
  expect(navigate).toHaveBeenCalledWith('/sign-in')
})

it('closes the account menu on a press outside it', async () => {
  renderWith(member)
  await userEvent.click(screen.getByRole('button', { name: 'Account menu for member@acme.test, Member' }))

  await userEvent.click(document.body)

  expect(screen.queryByRole('menu')).not.toBeInTheDocument()
})

it('sends a visitor with no session to sign in, remembering the page to come back to', () => {
  routerState.pathname = '/pair'
  renderWith(SIGNED_OUT, <RequireSession />)

  expect(navigate).toHaveBeenCalledWith('/sign-in')
  expect(peekNext()).toBe('/pair')
  routerState.pathname = '/'
})

it('forgets the remembered page once someone signed in reaches a page', () => {
  rememberNext('/pair?id=p-123')

  renderWith(member, <RequireSession />)

  expect(peekNext()).toBe('/')
})
