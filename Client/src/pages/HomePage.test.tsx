import { render } from '@testing-library/react'
import type { Workspace } from '../api'
import { WorkspacesContext } from '../workspace'
import { HomePage } from './HomePage'

jest.mock('react-router', () => jest.requireActual('../../jest/router').reactRouterMock())
const { navigate } = jest.requireActual('../../jest/router')

const workspace = (id: number, name: string): Workspace => ({ id, name, createdAt: '2026-11-16T00:00:00Z' })

const renderWith = (workspaces: Workspace[] | null) =>
  render(
    <WorkspacesContext.Provider value={{ workspaces, error: null, refresh: jest.fn() }}>
      <HomePage />
    </WorkspacesContext.Provider>,
  )

beforeEach(() => {
  jest.clearAllMocks()
  localStorage.clear()
})

it('opens the workspace last opened in this browser', () => {
  localStorage.setItem('nexus-workspace', '2')
  renderWith([workspace(1, 'Acme'), workspace(2, 'Finance')])

  expect(navigate).toHaveBeenCalledWith('/workspaces/2')
})

it('opens the first workspace by name when the last one opened is gone', () => {
  localStorage.setItem('nexus-workspace', '9')
  renderWith([workspace(1, 'Acme'), workspace(2, 'Finance')])

  expect(navigate).toHaveBeenCalledWith('/workspaces/1')
})

it('opens the Workspaces page when there is no workspace yet', () => {
  renderWith([])

  expect(navigate).toHaveBeenCalledWith('/workspaces')
})

it('waits for the workspaces before opening one', () => {
  renderWith(null)

  expect(navigate).not.toHaveBeenCalled()
})
