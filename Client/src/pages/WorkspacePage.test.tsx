import { render, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import {
  addConnection,
  addSyncs,
  type App,
  checkDestination,
  type Connection,
  type Destination,
  editApp,
  editSync,
  getWorkspace,
  listApps,
  listConnections,
  listConnectors,
  listSignInApps,
  listStreams,
  listSyncs,
  listUsers,
  makeApp,
  type Me,
  renewSignIn,
  replaceConnectionFile,
  runSync,
  saveSignInApp,
  type SignInApp,
  startSignIn,
  type Sync,
} from '../api'
import { leaveFor } from '../browser'
import { SessionContext } from '../session'
import { WorkspacePage } from './WorkspacePage'

jest.mock('react-router', () => jest.requireActual('../../jest/router').reactRouterMock())
const { routerState } = jest.requireActual('../../jest/router')

jest.mock('../api', () => ({
  ...jest.requireActual('../api'),
  getWorkspace: jest.fn(),
  listConnectors: jest.fn(),
  listConnections: jest.fn(),
  listApps: jest.fn(),
  listSyncs: jest.fn(),
  listSignInApps: jest.fn(),
  saveSignInApp: jest.fn(),
  listUsers: jest.fn(),
  addConnection: jest.fn(),
  listStreams: jest.fn(),
  addSyncs: jest.fn(),
  runSync: jest.fn(),
  editSync: jest.fn(),
  makeApp: jest.fn(),
  editApp: jest.fn(),
  checkDestination: jest.fn(),
  replaceConnectionFile: jest.fn(),
  startSignIn: jest.fn(),
  renewSignIn: jest.fn(),
}))

jest.mock('../browser', () => ({ leaveFor: jest.fn() }))

const member: Me = { signedIn: true, id: 2, email: 'member@acme.test', isAdmin: false, hasPassword: true }

const shop: Connection = { id: 9, name: 'Shop', connector: 'mysql', settings: { host: 'db.test' }, fileName: null, needsSignIn: false, createdAt: '2026-11-15T12:00:00Z' }

const scryIngest: Destination = {
  id: 7,
  url: 'http://localhost:6161/ingest',
  createdAt: '2026-11-15T12:00:00Z',
  keyName: 'Nexus',
  keyPrefix: 'scry_ab12',
  keyCheckedAt: '2026-11-15T12:00:00Z',
  keyProblem: null,
}

const scry: App = {
  id: 3,
  name: 'Scry',
  keyPrefix: 'nexus_abcdef',
  keyCreatedAt: '2026-11-15T13:00:00Z',
  returnUrls: [],
  createdAt: '2026-11-15T12:00:00Z',
  destinations: [scryIngest],
}

const reports: App = {
  id: 5,
  name: 'Reports',
  keyPrefix: null,
  keyCreatedAt: null,
  returnUrls: [],
  createdAt: '2026-11-14T12:00:00Z',
  destinations: [
    { id: 8, url: 'http://reports.test/ingest', createdAt: '2026-11-14T12:00:00Z', keyName: null, keyPrefix: null, keyCheckedAt: null, keyProblem: null },
  ],
}

const orders: Sync = {
  id: 11,
  connectionId: 9,
  connectionName: 'Shop',
  connectorName: 'MySQL',
  needsSignIn: false,
  destinationId: 7,
  destinationUrl: 'http://localhost:6161/ingest',
  appName: 'Scry',
  destinationKeyName: 'Nexus',
  destinationKeyPrefix: 'scry_ab12',
  stream: 'shop.orders',
  table: 'shop_orders',
  mode: 'FullRefresh',
  cursorField: null,
  status: 'Ready',
  lastRun: { id: 1, attempt: 1, status: 'Succeeded', startedAt: '2026-11-15T12:00:00Z', endedAt: '2026-11-15T12:01:00Z', rows: 1200, errorKind: null, error: null },
}

const NO_DESTINATION = 'Its destination was removed. Pick a new one to send it to.'

const orphaned: Sync = {
  ...orders,
  destinationId: null,
  destinationUrl: null,
  appName: null,
  destinationKeyName: null,
  destinationKeyPrefix: null,
  status: 'Failed',
  lastRun: { ...orders.lastRun!, status: 'Failed', rows: 0, errorKind: 'ConfigInvalid', error: NO_DESTINATION },
}

function renderAs(me: Me) {
  render(
    <SessionContext.Provider value={{ me, setMe: jest.fn() }}>
      <WorkspacePage />
    </SessionContext.Provider>,
  )
}

const rowOf = async (table: string) => (await screen.findByText(table, { selector: 'strong' })).closest('li')!

beforeEach(() => {
  jest.clearAllMocks()
  routerState.params = { id: '4' }
  jest.mocked(getWorkspace).mockResolvedValue({ id: 4, name: 'Acme', createdAt: '2026-11-15T12:00:00Z', members: [] })
  jest.mocked(listConnectors).mockResolvedValue([
    {
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
  ])
  jest.mocked(listConnections).mockResolvedValue([shop])
  jest.mocked(listSignInApps).mockResolvedValue([])
  jest.mocked(listApps).mockResolvedValue([scry])
  jest.mocked(listSyncs).mockResolvedValue([orders])
  jest.mocked(listUsers).mockResolvedValue([])
  jest.mocked(listStreams).mockResolvedValue([
    { name: 'shop.orders', columns: [], primaryKey: ['id'], cursorFields: ['id', 'updated_at'], syncModes: ['FullRefresh', 'Incremental'] },
  ])
})

it('adds a connection from the settings its connector asks for', async () => {
  jest.mocked(addConnection).mockResolvedValue({ ...shop, id: 10, name: 'Warehouse' })
  renderAs(member)
  await userEvent.click(await screen.findByRole('button', { name: 'Add connection' }))

  await userEvent.type(screen.getByLabelText('Name'), 'Warehouse')
  await userEvent.type(screen.getByLabelText('Host'), 'wh.test')
  await userEvent.type(screen.getByLabelText('Password'), 'hunter2')
  await userEvent.click(screen.getByRole('button', { name: 'Add connection' }))

  expect(addConnection).toHaveBeenCalledWith(4, 'Warehouse', 'mysql', { host: 'wh.test', password: 'hunter2' }, undefined)
  expect(await screen.findByRole('listitem', { name: 'Connection Warehouse' })).toBeInTheDocument()
})

const csvConnector = {
  id: 'csv',
  name: 'CSV',
  description: 'Reads a CSV file.',
  auth: 'FileUpload',
  fields: [
    { name: 'file', label: 'CSV file', kind: 'File' as const, required: true, choices: null, default: null },
    { name: 'stream', label: 'Table name', kind: 'Text' as const, required: true, choices: null, default: null },
    { name: 'header', label: 'First row holds column names', kind: 'Boolean' as const, required: false, choices: null, default: 'true' },
  ],
  notReady: null,
}

const ordersFile: Connection = { ...shop, id: 12, name: 'Orders', connector: 'csv', settings: { stream: 'orders', header: 'true' }, fileName: 'orders.csv' }

it("adds a CSV connection from an uploaded file, starting from its settings' defaults", async () => {
  jest.mocked(listConnectors).mockResolvedValue([csvConnector])
  jest.mocked(addConnection).mockResolvedValue(ordersFile)
  const file = new File(['id,item\n1,pen\n'], 'orders.csv', { type: 'text/csv' })
  renderAs(member)
  await userEvent.click(await screen.findByRole('button', { name: 'Add connection' }))

  await userEvent.type(screen.getByLabelText('Name'), 'Orders')
  await userEvent.upload(screen.getByLabelText('CSV file'), file)
  await userEvent.type(screen.getByLabelText('Table name'), 'orders')
  expect(screen.getByLabelText('First row holds column names')).toBeChecked()
  await userEvent.click(screen.getByLabelText('First row holds column names'))
  await userEvent.click(screen.getByRole('button', { name: 'Add connection' }))

  expect(addConnection).toHaveBeenCalledWith(4, 'Orders', 'csv', { header: 'false', stream: 'orders' }, file)
  const card = await screen.findByRole('listitem', { name: 'Connection Orders' })
  expect(within(card).getByText(/^CSV, file orders\.csv/)).toBeInTheDocument()
})

it("replaces a connection's file, and reads its syncs again", async () => {
  jest.mocked(listConnectors).mockResolvedValue([csvConnector])
  jest.mocked(listConnections).mockResolvedValue([ordersFile])
  jest.mocked(listSyncs).mockResolvedValue([])
  jest.mocked(replaceConnectionFile).mockResolvedValue({ ...ordersFile, fileName: 'orders-2.csv' })
  const file = new File(['id,item\n2,ink\n'], 'orders-2.csv', { type: 'text/csv' })
  renderAs(member)
  const card = await screen.findByRole('listitem', { name: 'Connection Orders' })

  await userEvent.upload(within(card).getByLabelText('New copy of orders.csv'), file)

  expect(replaceConnectionFile).toHaveBeenCalledWith(12, file)
  expect(await within(card).findByText(/^CSV, file orders-2\.csv/)).toBeInTheDocument()
  expect(listSyncs).toHaveBeenCalledTimes(2)
})

it("adds tables from a connection's streams into a destination", async () => {
  jest.mocked(listStreams).mockResolvedValue([
    { name: 'shop.customers', columns: [], primaryKey: ['id'], cursorFields: ['id'], syncModes: ['FullRefresh', 'Incremental'] },
  ])
  jest.mocked(addSyncs).mockResolvedValue([{ ...orders, id: 12, stream: 'shop.customers', table: 'shop_customers', status: 'Queued', lastRun: null }])
  renderAs(member)
  const card = await screen.findByRole('listitem', { name: 'Connection Shop' })
  await userEvent.click(within(card).getByRole('button', { name: 'Add tables' }))

  await userEvent.click(within(card).getByRole('button', { name: 'List its streams' }))
  await userEvent.click(await within(card).findByLabelText('shop.customers'))
  expect(within(card).getByLabelText('Table')).toHaveValue('shop_customers')
  await userEvent.selectOptions(within(card).getByLabelText('Read'), 'Incremental')
  await userEvent.click(within(card).getByRole('button', { name: 'Add sync' }))

  expect(listStreams).toHaveBeenCalledWith(9)
  expect(addSyncs).toHaveBeenCalledWith(9, 7, [{ stream: 'shop.customers', table: 'shop_customers', mode: 'Incremental' }])
  expect(await within(card).findByText('shop_customers', { selector: 'strong' })).toBeInTheDocument()
})

it('says what each table reads and where it writes, and runs one now', async () => {
  jest.mocked(runSync).mockResolvedValue({ ...orders, status: 'Queued' })
  renderAs(member)
  const row = await rowOf('shop_orders')
  expect(within(row).getByText('Reads shop.orders from Shop (MySQL), full refresh.')).toBeInTheDocument()
  expect(within(row).getByText('Writes into Scry at http://localhost:6161/ingest (destination 7, key scry_ab12…, named Nexus).')).toBeInTheDocument()
  expect(within(row).getByText(/Sent 1,200 rows/)).toBeInTheDocument()

  await userEvent.click(within(row).getByRole('button', { name: 'Run now' }))

  expect(runSync).toHaveBeenCalledWith(11)
  expect(await within(row).findByText('Queued')).toBeInTheDocument()
  expect(within(row).getByRole('button', { name: 'Run now' })).toBeDisabled()
})

it("links each connection and each table to its own entries on the Log page", async () => {
  renderAs(member)
  const row = await rowOf('shop_orders')

  expect(within(row).getByRole('link', { name: 'Log for sync shop_orders' })).toHaveAttribute('href', '/log?sync=11')
  expect(screen.getByRole('link', { name: 'Log for connection Shop' })).toHaveAttribute('href', '/log?connection=9')
})

it("edits a table's destination, name, and way of reading", async () => {
  jest.mocked(listApps).mockResolvedValue([reports, scry])
  jest.mocked(editSync).mockResolvedValue({
    ...orders,
    destinationId: 8,
    destinationUrl: 'http://reports.test/ingest',
    appName: 'Reports',
    destinationKeyName: null,
    destinationKeyPrefix: null,
    table: 'orders',
    mode: 'Incremental',
    cursorField: 'updated_at',
    status: 'Queued',
  })
  renderAs(member)
  const row = await rowOf('shop_orders')

  await userEvent.click(within(row).getByRole('button', { name: 'Edit' }))
  const into = within(row).getByLabelText('Into')
  expect(within(into).getAllByRole('option').map((o) => o.textContent)).toEqual([
    expect.stringMatching(/^Reports at http:\/\/reports\.test\/ingest \(destination 8, added .+, key not checked yet\)$/),
    expect.stringMatching(/^Scry at http:\/\/localhost:6161\/ingest \(destination 7, added .+, key scry_ab12…, named Nexus\)$/),
  ])
  await userEvent.selectOptions(into, '8')
  await userEvent.clear(within(row).getByLabelText('Table'))
  await userEvent.type(within(row).getByLabelText('Table'), 'orders')
  await userEvent.selectOptions(within(row).getByLabelText('Read'), 'Incremental')
  await userEvent.selectOptions(await within(row).findByLabelText('Track'), await within(row).findByRole('option', { name: 'updated_at' }))
  await userEvent.click(within(row).getByRole('button', { name: 'Save' }))

  expect(editSync).toHaveBeenCalledWith(11, { destinationId: 8, table: 'orders', mode: 'Incremental', cursorField: 'updated_at' })
  const moved = await rowOf('orders')
  expect(within(moved).getByText('Writes into Reports at http://reports.test/ingest (destination 8, key not checked yet).')).toBeInTheDocument()
  expect(within(moved).getByText('Reads shop.orders from Shop (MySQL), incremental, tracking updated_at.')).toBeInTheDocument()
  expect(within(moved).queryByRole('button', { name: 'Save' })).not.toBeInTheDocument()
})

it('shows an edit refused by the service in its form', async () => {
  jest.mocked(editSync).mockRejectedValue(new Error('The destination already has a sync into \'shop_orders\'. Pick another table name.'))
  jest.mocked(listApps).mockResolvedValue([reports, scry])
  renderAs(member)
  const row = await rowOf('shop_orders')

  await userEvent.click(within(row).getByRole('button', { name: 'Edit' }))
  await userEvent.selectOptions(within(row).getByLabelText('Into'), '8')
  await userEvent.click(within(row).getByRole('button', { name: 'Save' }))

  expect(await within(row).findByText(/already has a sync into 'shop_orders'/)).toBeInTheDocument()
  expect(within(row).getByRole('button', { name: 'Save' })).toBeEnabled()
})

it('asks for a new destination for a table whose destination was removed', async () => {
  jest.mocked(listSyncs).mockResolvedValue([orphaned])
  jest.mocked(editSync).mockResolvedValue({ ...orders, status: 'Queued' })
  renderAs(member)
  const row = await rowOf('shop_orders')

  expect(within(row).getByText(NO_DESTINATION, { selector: 'p.error' })).toBeInTheDocument()
  expect(within(row).getByRole('button', { name: 'Run now' })).toBeDisabled()
  expect(within(row).queryByRole('button', { name: 'Cancel' })).not.toBeInTheDocument()
  await userEvent.click(within(row).getByRole('button', { name: 'Save' }))

  expect(editSync).toHaveBeenCalledWith(11, { destinationId: 7, table: 'shop_orders', mode: 'FullRefresh', cursorField: null })
  const sent = await rowOf('shop_orders')
  expect(within(sent).getByText(/^Writes into Scry at/)).toBeInTheDocument()
})

it('dates each app, its key, and its destinations, and names the push key each destination sends', async () => {
  jest.mocked(listApps).mockResolvedValue([reports, scry])
  renderAs(member)
  const card = (await screen.findByText('Scry', { selector: 'strong' })).closest('li')!
  expect(within(card).getByText(/^Made .+\. Key nexus_abcdef…, made .+\.$/)).toBeInTheDocument()
  expect(within(card).getByText(/^\(id 7, added .+\)$/)).toBeInTheDocument()
  expect(within(card).getByText(/^Push key scry_ab12…, named Nexus, checked .+$/)).toBeInTheDocument()
  const unkeyed = screen.getByText('Reports', { selector: 'strong' }).closest('li')!
  expect(within(unkeyed).getByText(/^Made .+\. No key\.$/)).toBeInTheDocument()
  expect(within(unkeyed).getByText('Push key not checked yet')).toBeInTheDocument()
})

it("lets an admin check a destination's key", async () => {
  jest.mocked(checkDestination).mockResolvedValue({
    ...scryIngest,
    keyCheckedAt: '2026-11-16T09:00:00Z',
    keyProblem: 'The push API refused the key. Send a push API key.',
  })
  renderAs({ ...member, isAdmin: true })
  const card = (await screen.findByText('Scry', { selector: 'strong' })).closest('li')!

  await userEvent.click(within(card).getByRole('button', { name: 'Check key' }))

  expect(checkDestination).toHaveBeenCalledWith(7)
  expect(await within(card).findByText(/^Key refused at .+: The push API refused the key\. Send a push API key\.$/)).toBeInTheDocument()
})

it('offers a member no app changes', async () => {
  renderAs(member)
  await screen.findByText('Scry', { selector: 'strong' })
  expect(screen.queryByRole('button', { name: 'New key' })).not.toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Check key' })).not.toBeInTheDocument()
  expect(screen.queryByLabelText('New app')).not.toBeInTheDocument()
})

it("lets an admin set an app's return addresses, and shows each destination's id", async () => {
  jest.mocked(editApp).mockResolvedValue({ ...scry, returnUrls: ['http://localhost:6160/sources'] })
  renderAs({ ...member, isAdmin: true })
  expect(await screen.findByText(/^\(id 7, added .+\)$/)).toBeInTheDocument()

  await userEvent.type(screen.getByLabelText('Return addresses, one a line'), 'http://localhost:6160/sources')
  await userEvent.click(screen.getByRole('button', { name: 'Save return addresses' }))

  expect(editApp).toHaveBeenCalledWith(3, 'Scry', ['http://localhost:6160/sources'])
  expect(await screen.findByText('Returns to http://localhost:6160/sources')).toBeInTheDocument()
})

it('lets an admin make an app and shows its key once', async () => {
  jest.mocked(makeApp).mockResolvedValue({ app: { ...scry, id: 8, name: 'Reports', destinations: [] }, key: 'nexus_the-whole-key' })
  renderAs({ ...member, isAdmin: true })
  await screen.findByText('Scry', { selector: 'strong' })

  await userEvent.type(screen.getByLabelText('New app'), 'Reports')
  await userEvent.click(screen.getByRole('button', { name: 'Make app' }))

  expect(makeApp).toHaveBeenCalledWith(4, 'Reports', [])
  expect(await screen.findByLabelText("Reports's key")).toHaveValue('nexus_the-whole-key')
  expect(screen.getByRole('button', { name: 'Copy key' })).toBeInTheDocument()
  await userEvent.click(screen.getByRole('button', { name: 'Done' }))
  expect(screen.queryByLabelText("Reports's key")).not.toBeInTheDocument()
})

const schwabConnector = {
  id: 'schwab',
  name: 'Charles Schwab',
  description: 'Reads a Schwab sign-in.',
  auth: 'OAuth',
  fields: [{ name: 'startDate', label: 'Read history from (yyyy-MM-dd)', kind: 'Text' as const, required: true, choices: null, default: null }],
  notReady: null,
}

const schwabApp: SignInApp = {
  connector: 'schwab',
  name: 'Charles Schwab',
  set: false,
  configured: false,
  keyEnding: null,
  updatedAt: null,
  callbackUrl: 'https://127.0.0.1:6164/oauth/callback',
  problem: 'This workspace has no Charles Schwab app yet. Add it under Sign-in apps on the workspace page in Nexus.',
}

it('sets the workspace app a provider signs in through, showing the callback to register', async () => {
  const set: SignInApp = { ...schwabApp, set: true, keyEnding: '1a2b', updatedAt: '2026-11-16T12:00:00Z', problem: null }
  jest.mocked(listSignInApps).mockResolvedValueOnce([schwabApp]).mockResolvedValue([set])
  jest.mocked(saveSignInApp).mockResolvedValue(set)
  renderAs(member)

  expect(await screen.findByText('Not set yet.')).toBeInTheDocument()
  expect(screen.getByLabelText('Callback to register on the app')).toHaveValue('https://127.0.0.1:6164/oauth/callback')
  await userEvent.type(screen.getByLabelText('App key'), 'key-1a2b')
  await userEvent.type(screen.getByLabelText('App secret'), 'the-secret')
  await userEvent.click(screen.getByRole('button', { name: 'Save app' }))

  expect(saveSignInApp).toHaveBeenCalledWith(4, 'schwab', 'key-1a2b', 'the-secret')
  expect(await screen.findByText(/^Set, with the key ending 1a2b/)).toBeInTheDocument()
  expect(screen.getByLabelText('App secret')).toHaveValue('')
  expect(screen.getByLabelText('App secret')).toHaveAttribute('placeholder', 'Stored')
})

it("says a provider cannot be signed in to until the workspace's app is set, in place of the sign-in button", async () => {
  jest.mocked(listConnectors).mockResolvedValue([schwabConnector])
  jest.mocked(listSignInApps).mockResolvedValue([schwabApp])
  renderAs(member)

  await userEvent.click(await screen.findByRole('button', { name: 'Add connection' }))

  expect(screen.getByText(schwabApp.problem!, { selector: 'form p' })).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Sign in to Charles Schwab' })).not.toBeInTheDocument()
})

it('signs in to a provider for a connector that signs in, and says so on coming back', async () => {
  jest.mocked(listConnectors).mockResolvedValue([schwabConnector])
  jest.mocked(startSignIn).mockResolvedValue({ url: 'https://api.schwabapi.com/v1/oauth/authorize?state=abc' })
  window.history.pushState({}, '', '/workspaces/4?signedIn=9')
  renderAs(member)

  expect(await screen.findByText('Signed in. Its tables are added from its card below.')).toBeInTheDocument()
  expect(window.location.search).toBe('')
  await userEvent.click(screen.getByRole('button', { name: 'Add connection' }))
  await userEvent.type(screen.getByLabelText('Name'), 'Brokerage')
  await userEvent.type(screen.getByLabelText('Read history from (yyyy-MM-dd)'), '2026-01-01')
  await userEvent.click(screen.getByRole('button', { name: 'Sign in to Charles Schwab' }))

  expect(startSignIn).toHaveBeenCalledWith(4, 'Brokerage', 'schwab', { startDate: '2026-01-01' })
  expect(leaveFor).toHaveBeenCalledWith('https://api.schwabapi.com/v1/oauth/authorize?state=abc')
  expect(addConnection).not.toHaveBeenCalled()
})

it('signs a connection in again once its provider ends its sign-in, and shows why one failed', async () => {
  window.history.pushState({}, '', `/workspaces/4?error=${encodeURIComponent('Schwab refused the sign-in (400): Bad code.')}`)
  jest.mocked(listConnectors).mockResolvedValue([schwabConnector])
  jest.mocked(listConnections).mockResolvedValue([{ ...shop, connector: 'schwab', settings: { startDate: '2026-01-01' }, needsSignIn: true }])
  jest.mocked(renewSignIn).mockResolvedValue({ url: 'https://api.schwabapi.com/v1/oauth/authorize?state=def' })
  renderAs(member)

  expect(await screen.findByText('Schwab refused the sign-in (400): Bad code.')).toBeInTheDocument()
  const card = await screen.findByRole('listitem', { name: 'Connection Shop' })
  await userEvent.click(within(card).getByRole('button', { name: 'Sign in again' }))

  expect(renewSignIn).toHaveBeenCalledWith(9)
  expect(leaveFor).toHaveBeenCalledWith('https://api.schwabapi.com/v1/oauth/authorize?state=def')
})
