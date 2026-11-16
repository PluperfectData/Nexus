import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ApiError, cancelConnect, connectConnection, connectStreams, type ConnectView, finishConnect, getConnect } from '../api'
import { leaveFor } from '../browser'
import { ConnectPage } from './ConnectPage'

jest.mock('../api', () => ({
  ...jest.requireActual('../api'),
  getConnect: jest.fn(),
  connectConnection: jest.fn(),
  connectStreams: jest.fn(),
  finishConnect: jest.fn(),
  cancelConnect: jest.fn(),
}))

jest.mock('../browser', () => ({ leaveFor: jest.fn() }))

const opened: ConnectView = {
  appName: 'Scry',
  workspaceName: 'Acme',
  connector: {
    id: 'mysql',
    name: 'MySQL',
    description: 'Reads tables from MySQL.',
    auth: 'ConnectionFields',
    fields: [
      { name: 'host', label: 'Host', kind: 'Text', required: true, choices: null },
      { name: 'password', label: 'Password', kind: 'Secret', required: false, choices: null },
    ],
  },
  destinationUrl: 'http://localhost:6161/ingest',
  expiresAt: '2026-11-15T12:10:00Z',
  connection: null,
}

const connected: ConnectView = {
  ...opened,
  connection: { id: 9, name: 'Shop', connector: 'mysql', settings: { host: 'db.test' }, needsSignIn: false, createdAt: '2026-11-15T12:01:00Z' },
}

beforeEach(() => {
  jest.clearAllMocks()
  window.location.hash = '#nxc_the-token'
})

it('connects with the settings its connector asks for, then sends the picked streams and goes back to the app', async () => {
  jest.mocked(getConnect).mockResolvedValue(opened)
  jest.mocked(connectConnection).mockResolvedValue(connected)
  jest.mocked(connectStreams).mockResolvedValue([
    { name: 'shop.orders', columns: [], primaryKey: ['id'], cursorFields: ['id'], syncModes: ['FullRefresh', 'Incremental'] },
  ])
  jest.mocked(finishConnect).mockResolvedValue({ returnUrl: 'http://localhost:6160/sources?connection=9' })
  render(<ConnectPage />)

  expect(await screen.findByRole('heading', { name: 'Connect MySQL' })).toBeInTheDocument()
  expect(getConnect).toHaveBeenCalledWith('nxc_the-token')
  await userEvent.type(screen.getByLabelText('Name'), 'Shop')
  await userEvent.type(screen.getByLabelText('Host'), 'db.test')
  await userEvent.type(screen.getByLabelText('Password'), 'hunter2')
  await userEvent.click(screen.getByRole('button', { name: 'Connect' }))
  expect(connectConnection).toHaveBeenCalledWith('nxc_the-token', 'Shop', { host: 'db.test', password: 'hunter2' })

  await userEvent.click(await screen.findByLabelText('shop.orders'))
  expect(screen.getByLabelText('Table')).toHaveValue('shop_orders')
  await userEvent.click(screen.getByRole('button', { name: 'Finish and go back to Scry' }))

  expect(finishConnect).toHaveBeenCalledWith('nxc_the-token', [{ stream: 'shop.orders', table: 'shop_orders', mode: 'FullRefresh' }])
  expect(leaveFor).toHaveBeenCalledWith('http://localhost:6160/sources?connection=9')
})

it('says why settings did not connect, and lets the person try again', async () => {
  jest.mocked(getConnect).mockResolvedValue(opened)
  jest.mocked(connectConnection).mockRejectedValue(new ApiError(400, 'Could not reach db.test.'))
  render(<ConnectPage />)

  await userEvent.type(await screen.findByLabelText('Name'), 'Shop')
  await userEvent.type(screen.getByLabelText('Host'), 'db.test')
  await userEvent.click(screen.getByRole('button', { name: 'Connect' }))

  expect(await screen.findByText('Could not reach db.test.')).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Connect' })).toBeEnabled()
})

it('cancels back to the app', async () => {
  jest.mocked(getConnect).mockResolvedValue(opened)
  jest.mocked(cancelConnect).mockResolvedValue({ returnUrl: 'http://localhost:6160/sources?error=cancelled' })
  render(<ConnectPage />)

  await userEvent.click(await screen.findByRole('button', { name: 'Cancel' }))

  expect(leaveFor).toHaveBeenCalledWith('http://localhost:6160/sources?error=cancelled')
})

it('says an expired page has expired', async () => {
  jest.mocked(getConnect).mockRejectedValue(
    new ApiError(401, 'This connect page has expired or is finished. Go back to the app and start again.'),
  )
  render(<ConnectPage />)

  expect(await screen.findByText('This connect page has expired or is finished. Go back to the app and start again.')).toBeInTheDocument()
})
