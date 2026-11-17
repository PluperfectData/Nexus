import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { getInstall, getSettings, type InstallView, type Me, saveAddresses, saveDatabase, saveLogDays, type Settings, testMail } from '../api'
import { SessionContext } from '../session'
import { SettingsPage } from './SettingsPage'

jest.mock('react-router', () => jest.requireActual('../../jest/router').reactRouterMock())
const { navigate } = jest.requireActual('../../jest/router')

jest.mock('../api', () => ({
  ...jest.requireActual('../api'),
  getSettings: jest.fn(),
  saveAddresses: jest.fn(),
  saveMail: jest.fn(),
  testMail: jest.fn(),
  saveDatabase: jest.fn(),
  saveLogDays: jest.fn(),
  getInstall: jest.fn(),
}))

const admin: Me = { signedIn: true, id: 1, email: 'admin@acme.test', isAdmin: true, hasPassword: true }

const settings: Settings = {
  clientOrigin: 'http://localhost',
  clientOriginConfigured: false,
  callbackOrigin: null,
  callbackOriginConfigured: false,
  callbackUrl: null,
  callbackSuggestion: 'https://127.0.0.1:6164',
  mail: {
    transport: 'Postmark',
    tokenSet: true,
    tokenConfigured: false,
    from: 'nexus@acme.test',
    fromConfigured: false,
    fromName: 'Nexus',
    fromNameConfigured: false,
    folder: null,
  },
  database: { host: 'localhost', port: 3306, name: 'nexus', user: 'nexus' },
  databaseConfigured: false,
  logDays: 30,
  logDaysConfigured: false,
}

const notInstalled: InstallView = {
  installed: false,
  stage: 'Admin',
  local: true,
  allowed: true,
  databaseConfigured: false,
  database: null,
  databaseProblem: null,
  clientOriginConfigured: false,
  mail: true,
  mailSettings: null,
}

const renderAs = (me: Me) =>
  render(
    <SessionContext.Provider value={{ me, setMe: jest.fn() }}>
      <SettingsPage />
    </SessionContext.Provider>,
  )

beforeEach(() => {
  jest.clearAllMocks()
  jest.restoreAllMocks()
  jest.mocked(getSettings).mockResolvedValue(settings)
})

it('saves how many days the log keeps an entry', async () => {
  jest.mocked(saveLogDays).mockResolvedValue({ ...settings, logDays: 90 })
  renderAs(admin)

  const days = await screen.findByLabelText('Keep entries for this many days')
  expect(days).toHaveValue(30)
  await userEvent.clear(days)
  await userEvent.type(days, '90')
  await userEvent.click(screen.getByRole('button', { name: 'Save' }))

  expect(saveLogDays).toHaveBeenCalledWith(90)
  expect(await screen.findByText('Saved.')).toBeInTheDocument()
})

it('shows the days a configured instance keeps, with nothing to change', async () => {
  jest.mocked(getSettings).mockResolvedValue({ ...settings, logDays: 7, logDaysConfigured: true })
  renderAs(admin)

  expect(await screen.findByText("The service's configuration keeps log entries for 7 days, in Nexus:LogDays.")).toBeInTheDocument()
  expect(screen.queryByLabelText('Keep entries for this many days')).not.toBeInTheDocument()
})

it('starts the callback from the service, saves the addresses, and shows the callback to register', async () => {
  jest.mocked(saveAddresses).mockResolvedValue({
    ...settings,
    callbackOrigin: 'https://127.0.0.1:6164',
    callbackUrl: 'https://127.0.0.1:6164/oauth/callback',
  })
  renderAs(admin)

  expect(await screen.findByLabelText('Callback address')).toHaveValue('https://127.0.0.1:6164')
  await userEvent.click(screen.getByRole('button', { name: 'Save addresses' }))

  expect(saveAddresses).toHaveBeenCalledWith('http://localhost', 'https://127.0.0.1:6164')
  expect(await screen.findByLabelText('Callback to register with each provider')).toHaveValue('https://127.0.0.1:6164/oauth/callback')
})

it('asks before moving the client away from this page, and keeps the address when told no', async () => {
  jest.spyOn(window, 'confirm').mockReturnValue(false)
  renderAs(admin)

  const client = await screen.findByLabelText('Client address')
  await userEvent.clear(client)
  await userEvent.type(client, 'https://nexus.test')
  await userEvent.click(screen.getByRole('button', { name: 'Save addresses' }))

  expect(window.confirm).toHaveBeenCalledWith('Pages at http://localhost stop reaching Nexus once its client is https://nexus.test. Save anyway?')
  expect(saveAddresses).not.toHaveBeenCalled()
})

it('keeps the token write-only and sends a test mail', async () => {
  jest.mocked(testMail).mockResolvedValue({ sentTo: 'admin@acme.test', transport: 'Postmark', folder: null })
  renderAs(admin)

  const token = await screen.findByLabelText('Postmark server token')
  await userEvent.click(screen.getByRole('button', { name: 'Send a test mail' }))

  expect(token).toHaveValue('')
  expect(token).toHaveAttribute('placeholder', 'Stored')
  expect(await screen.findByText('Sent a test mail to admin@acme.test.')).toBeInTheDocument()
})

it('shows a value the configuration sets without offering to change it', async () => {
  jest.mocked(getSettings).mockResolvedValue({ ...settings, clientOriginConfigured: true, databaseConfigured: true })
  renderAs(admin)

  expect(await screen.findByLabelText('Client address')).toBeDisabled()
  expect(screen.getByText("Set by the service's configuration.")).toBeInTheDocument()
  expect(screen.getByText(/The service's configuration sets the database, in ConnectionStrings:Nexus: nexus on localhost:3306/)).toBeInTheDocument()
  expect(screen.queryByRole('button', { name: 'Test and use this database' })).not.toBeInTheDocument()
})

it('moves to another database after a confirm, and goes to the installer when that one has no admin', async () => {
  jest.spyOn(window, 'confirm').mockReturnValue(true)
  jest.mocked(saveDatabase).mockResolvedValue({ ...settings, database: { host: 'db.moved', port: 3306, name: 'nexus', user: 'nexus' } })
  jest.mocked(getInstall).mockResolvedValue(notInstalled)
  renderAs(admin)

  const host = await screen.findByLabelText('Host')
  await userEvent.clear(host)
  await userEvent.type(host, 'db.moved')
  await userEvent.click(screen.getByRole('button', { name: 'Test and use this database' }))

  expect(window.confirm).toHaveBeenCalledWith('Move Nexus to nexus on db.moved? It shows the records there from now on.')
  expect(saveDatabase).toHaveBeenCalledWith({ host: 'db.moved', port: 3306, name: 'nexus', user: 'nexus', password: '' })
  await screen.findByText('Nexus uses this database now.')
  expect(navigate).toHaveBeenCalledWith('/install')
})

it('tells a member that only admins change the settings', () => {
  renderAs({ ...admin, isAdmin: false })

  expect(screen.getByText("Only admins change the instance's settings.")).toBeInTheDocument()
  expect(getSettings).not.toHaveBeenCalled()
})
