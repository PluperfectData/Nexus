import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import type { Me, Workspace } from '../api'
import { peekNext, rememberNext } from '../next'
import { SessionContext, SIGNED_OUT } from '../session'
import { WorkspacesContext } from '../workspace'
import { Layout, RequireSession } from './Layout'

jest.mock('react-router', () => jest.requireActual('../../jest/router').reactRouterMock())
const { navigate, routerState } = jest.requireActual('../../jest/router')

jest.mock('../api', () => ({ ...jest.requireActual('../api'), signOut: jest.fn().mockResolvedValue(null) }))

const member: Me = { signedIn: true, id: 2, email: 'member@acme.test', isAdmin: false, hasPassword: true }

const workspace = (id: number, name: string): Workspace => ({ id, name, createdAt: '2026-11-16T00:00:00Z' })

const theirs = [workspace(1, 'Acme'), workspace(2, 'Finance')]

const renderWith = (me: Me | null, element = <Layout />, setMe = jest.fn(), workspaces: Workspace[] = theirs) =>
  render(
    <SessionContext.Provider value={{ me, setMe }}>
      <WorkspacesContext.Provider value={{ workspaces, error: null, refresh: jest.fn() }}>{element}</WorkspacesContext.Provider>
    </SessionContext.Provider>,
  )

beforeEach(() => {
  jest.clearAllMocks()
  localStorage.clear()
  routerState.pathname = '/'
})

it("keeps the Log alone in the nav, for everyone, and puts an admin's pages in the account menu", async () => {
  const { unmount } = renderWith(member)
  const nav = screen.getByRole('navigation', { name: 'Main' })
  expect(within(nav).getAllByRole('link').map((link) => `${link.textContent} ${link.getAttribute('href')}`)).toEqual(['Log /log'])
  await userEvent.click(screen.getByRole('button', { name: /Account menu/ }))
  expect(within(screen.getByRole('menu', { name: 'Account' })).queryByRole('menuitem', { name: 'Users' })).not.toBeInTheDocument()
  unmount()

  renderWith({ ...member, isAdmin: true })
  expect(within(screen.getByRole('navigation', { name: 'Main' })).getAllByRole('link').map((link) => link.textContent)).toEqual(['Log'])
  await userEvent.click(screen.getByRole('button', { name: /Account menu/ }))
  const menu = screen.getByRole('menu', { name: 'Account' })
  expect(within(menu).getByRole('menuitem', { name: 'Users' })).toHaveAttribute('href', '/users')
  expect(within(menu).getByRole('menuitem', { name: 'Workspaces' })).toHaveAttribute('href', '/workspaces')
  expect(within(menu).getByRole('menuitem', { name: 'Settings' })).toHaveAttribute('href', '/settings')
})

describe('the workspace menu', () => {
  it("names the workspace whose page is open, and switches to another", async () => {
    routerState.pathname = '/workspaces/2'
    renderWith(member)

    await userEvent.click(screen.getByRole('button', { name: 'Workspace: Finance' }))
    const menu = screen.getByRole('menu', { name: 'Workspaces' })

    expect(within(menu).getByRole('menuitem', { name: 'Finance' })).toHaveAttribute('aria-current', 'page')
    expect(within(menu).getByRole('menuitem', { name: 'Acme' })).toHaveAttribute('href', '/workspaces/1')
    expect(within(menu).queryByRole('menuitem', { name: 'Manage workspaces' })).not.toBeInTheDocument()
  })

  it('names the workspace on any of its tabs', () => {
    routerState.pathname = '/workspaces/2/apps'
    renderWith(member)

    expect(screen.getByRole('button', { name: 'Workspace: Finance' })).toBeInTheDocument()
  })

  it('names the workspace last opened on a page outside any, such as the Log', () => {
    localStorage.setItem('nexus-workspace', '2')
    routerState.pathname = '/log'
    renderWith(member)

    expect(screen.getByRole('button', { name: 'Workspace: Finance' })).toBeInTheDocument()
  })

  it('offers an admin a way to manage the workspaces', async () => {
    renderWith({ ...member, isAdmin: true })

    await userEvent.click(screen.getByRole('button', { name: 'Workspace: Acme' }))

    expect(within(screen.getByRole('menu', { name: 'Workspaces' })).getByRole('menuitem', { name: 'Manage workspaces' })).toHaveAttribute('href', '/workspaces')
  })

  it('is left out for someone in no workspace', () => {
    renderWith(member, <Layout />, jest.fn(), [])

    expect(screen.queryByRole('button', { name: /^Workspace:/ })).not.toBeInTheDocument()
  })
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
