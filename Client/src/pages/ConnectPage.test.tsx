import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { ApiError, cancelConnect, connectConnection, connectSignIn, connectStreams, type ConnectView, finishConnect, getConnect } from '../api'
import { leaveFor } from '../browser'
import { ConnectPage } from './ConnectPage'

jest.mock('../api', () => ({
  ...jest.requireActual('../api'),
  getConnect: jest.fn(),
  connectConnection: jest.fn(),
  connectSignIn: jest.fn(),
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
      { name: 'host', label: 'Host', kind: 'Text', required: true, choices: null, default: null },
      { name: 'password', label: 'Password', kind: 'Secret', required: false, choices: null, default: null },
    ],
    notReady: null,
  },
  destinationUrl: 'http://localhost:6161/ingest',
  expiresAt: '2026-11-15T12:10:00Z',
  connection: null,
  renewing: false,
}

const connected: ConnectView = {
  ...opened,
  connection: { id: 9, name: 'Shop', connector: 'mysql', settings: { host: 'db.test' }, fileName: null, signIn: null, createdAt: '2026-11-15T12:01:00Z' },
}

beforeEach(() => {
  jest.clearAllMocks()
  window.location.hash = '#nxc_the-token'
})

const schwab = {
  id: 'schwab',
  name: 'Charles Schwab',
  description: 'Reads a Schwab sign-in.',
  auth: 'OAuth',
  fields: [{ name: 'startDate', label: 'Read history from (yyyy-MM-dd)', kind: 'Text' as const, required: true, choices: null, default: null }],
  notReady: null,
}

it("says the workspace's app is missing in place of the sign-in button", async () => {
  const notReady = 'This workspace has no Charles Schwab app yet. Add it under Providers on the workspace page in Nexus.'
  jest.mocked(getConnect).mockResolvedValue({ ...opened, connector: { ...schwab, notReady } })
  render(<ConnectPage />)

  expect(await screen.findByText(notReady)).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Sign in to Charles Schwab' })).not.toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Cancel' })).toBeInTheDocument()
})

it("signs in to a provider's page in place of connecting, for a connector that signs in", async () => {
  jest.mocked(getConnect).mockResolvedValue({ ...opened, connector: schwab })
  jest.mocked(connectSignIn).mockResolvedValue({ url: 'https://api.schwabapi.com/v1/oauth/authorize?state=abc' })
  render(<ConnectPage />)

  await userEvent.type(await screen.findByLabelText('Name'), 'Brokerage')
  await userEvent.type(screen.getByLabelText('Read history from (yyyy-MM-dd)'), '2026-01-01')
  await userEvent.click(screen.getByRole('button', { name: 'Sign in to Charles Schwab' }))

  expect(connectSignIn).toHaveBeenCalledWith('nxc_the-token', 'Brokerage', { startDate: '2026-01-01' })
  expect(leaveFor).toHaveBeenCalledWith('https://api.schwabapi.com/v1/oauth/authorize?state=abc')
  expect(connectConnection).not.toHaveBeenCalled()
})

it('signs a connection in again, and shows why a sign-in that came back failed', async () => {
  window.history.pushState({}, '', `/connect?error=${encodeURIComponent('Schwab did not sign you in: access_denied.')}#nxc_the-token`)
  jest.mocked(getConnect).mockResolvedValue({ ...connected, connector: schwab, renewing: true })
  jest.mocked(connectSignIn).mockResolvedValue({ url: 'https://api.schwabapi.com/v1/oauth/authorize?state=def' })
  render(<ConnectPage />)

  expect(await screen.findByText('Schwab did not sign you in: access_denied.')).toBeInTheDocument()
  expect(window.location.search).toBe('')
  expect(window.location.hash).toBe('#nxc_the-token')
  const again = await screen.findByRole('button', { name: 'Sign in to Charles Schwab again' })
  expect(screen.getByText('Schwab did not sign you in: access_denied.')).toBeInTheDocument()
  expect(connectStreams).not.toHaveBeenCalled()
  await userEvent.click(again)

  expect(connectSignIn).toHaveBeenCalledWith('nxc_the-token', '', {})
  expect(leaveFor).toHaveBeenCalledWith('https://api.schwabapi.com/v1/oauth/authorize?state=def')
})

it("uploads the file a CSV connector reads, starting from its settings' defaults", async () => {
  jest.mocked(getConnect).mockResolvedValue({
    ...opened,
    connector: {
      id: 'csv',
      name: 'CSV',
      description: 'Reads a CSV file.',
      auth: 'FileUpload',
      fields: [
        { name: 'file', label: 'CSV file', kind: 'File', required: true, choices: null, default: null },
        { name: 'stream', label: 'Table name', kind: 'Text', required: true, choices: null, default: null },
        { name: 'header', label: 'First row holds column names', kind: 'Boolean', required: false, choices: null, default: 'true' },
      ],
      notReady: null,
    },
  })
  jest.mocked(connectConnection).mockResolvedValue(connected)
  jest.mocked(connectStreams).mockResolvedValue([])
  const file = new File(['id,item\n1,pen\n'], 'orders.csv', { type: 'text/csv' })
  render(<ConnectPage />)

  await userEvent.type(await screen.findByLabelText('Name'), 'Orders')
  await userEvent.upload(screen.getByLabelText('CSV file'), file)
  await userEvent.type(screen.getByLabelText('Table name'), 'orders')
  expect(screen.getByLabelText('First row holds column names')).toBeChecked()
  await userEvent.click(screen.getByRole('button', { name: 'Connect' }))

  expect(connectConnection).toHaveBeenCalledWith('nxc_the-token', 'Orders', { header: 'true', stream: 'orders' }, file)
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
  expect(connectConnection).toHaveBeenCalledWith('nxc_the-token', 'Shop', { host: 'db.test', password: 'hunter2' }, undefined)

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
