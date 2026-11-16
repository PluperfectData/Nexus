import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import {
  askForInstallCode,
  getInstall,
  getSettings,
  installAddress,
  installAdmin,
  installDatabase,
  type InstallView,
  type Me,
  type Settings,
} from '../api'
import { SessionContext, SIGNED_OUT } from '../session'
import { InstallPage } from './InstallPage'

jest.mock('react-router', () => jest.requireActual('../../jest/router').reactRouterMock())
const { navigate } = jest.requireActual('../../jest/router')

jest.mock('../api', () => ({
  ...jest.requireActual('../api'),
  getInstall: jest.fn(),
  installDatabase: jest.fn(),
  installAddress: jest.fn(),
  installAdmin: jest.fn(),
  askForInstallCode: jest.fn(),
  getSettings: jest.fn(),
}))

const founder: Me = { signedIn: true, id: 1, email: 'founder@acme.test', isAdmin: true, hasPassword: true }

const view = (over: Partial<InstallView>): InstallView => ({
  installed: false,
  stage: 'Database',
  local: true,
  allowed: true,
  databaseConfigured: false,
  database: null,
  databaseProblem: null,
  clientOriginConfigured: false,
  mail: false,
  ...over,
})

const settings: Settings = {
  clientOrigin: 'http://localhost',
  clientOriginConfigured: false,
  callbackOrigin: null,
  callbackOriginConfigured: false,
  callbackUrl: null,
  callbackSuggestion: 'https://127.0.0.1:6164',
  mail: {
    transport: 'None',
    tokenSet: false,
    tokenConfigured: false,
    from: null,
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

function renderPage(setMe = jest.fn()) {
  render(
    <SessionContext.Provider value={{ me: SIGNED_OUT, setMe }}>
      <InstallPage />
    </SessionContext.Provider>,
  )
  return setMe
}

beforeEach(() => {
  jest.clearAllMocks()
  window.history.replaceState(null, '', '/install')
})

it('takes a page on this machine through the database, the address, and the first admin, then offers mail', async () => {
  jest.mocked(getInstall).mockResolvedValueOnce(view({})).mockResolvedValue(view({ installed: true, stage: 'Done' }))
  jest.mocked(installDatabase).mockResolvedValue(view({ stage: 'Address' }))
  jest.mocked(installAddress).mockResolvedValue(view({ stage: 'Admin' }))
  jest.mocked(installAdmin).mockResolvedValue(founder)
  jest.mocked(getSettings).mockResolvedValue(settings)
  const setMe = renderPage()

  await userEvent.type(await screen.findByLabelText('Password'), 'database-password')
  await userEvent.click(screen.getByRole('button', { name: 'Test and save' }))
  await userEvent.click(await screen.findByRole('button', { name: 'Save address' }))
  await userEvent.type(await screen.findByLabelText('Email'), 'founder@acme.test')
  await userEvent.type(screen.getByLabelText('Password'), 'correct horse battery')
  await userEvent.type(screen.getByLabelText('The same password again'), 'correct horse battery')
  await userEvent.click(screen.getByRole('button', { name: 'Make the admin and sign in' }))

  expect(installDatabase).toHaveBeenCalledWith(null, { host: 'localhost', port: 3306, name: 'nexus', user: 'nexus', password: 'database-password' })
  expect(installAddress).toHaveBeenCalledWith(null, 'http://localhost')
  expect(installAdmin).toHaveBeenCalledWith(null, 'founder@acme.test', 'correct horse battery')
  expect(setMe).toHaveBeenCalledWith(founder)
  expect(await screen.findByText('Step 4 of 4: Mail')).toBeInTheDocument()
  expect(screen.getByLabelText('Callback address')).toHaveValue('https://127.0.0.1:6164')
  await userEvent.click(screen.getByRole('button', { name: 'Done' }))
  expect(navigate).toHaveBeenCalledWith('/')
})

it('takes the install code from the link, and keeps it out of the address bar', async () => {
  window.history.replaceState(null, '', '/install#ABCD-EFGH-JKMN')
  jest.mocked(getInstall).mockResolvedValue(view({ local: false }))
  jest.mocked(installDatabase).mockResolvedValue(view({ stage: 'Address', local: false }))
  renderPage()

  await userEvent.click(await screen.findByRole('button', { name: 'Test and save' }))

  expect(getInstall).toHaveBeenCalledWith('ABCD-EFGH-JKMN')
  expect(installDatabase).toHaveBeenCalledWith('ABCD-EFGH-JKMN', expect.objectContaining({ host: 'localhost' }))
  expect(window.location.hash).toBe('')
})

it('asks a page on another machine for the code, and says when one is wrong', async () => {
  jest.mocked(getInstall).mockResolvedValue(view({ local: false, allowed: false }))
  renderPage()

  await userEvent.type(await screen.findByLabelText('Install code'), 'WXYZ-WXYZ-WXYZ')
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }))

  expect(getInstall).toHaveBeenLastCalledWith('WXYZ-WXYZ-WXYZ')
  expect(await screen.findByText("That is not the install code. Copy it from the service's log.")).toBeInTheDocument()
})

it('says why the stored database does not open, with its place filled in', async () => {
  jest.mocked(getInstall).mockResolvedValue(
    view({ databaseProblem: 'Unable to connect to any of the specified MySQL hosts.', database: { host: 'db.test', port: 3307, name: 'nx', user: 'ops' } }),
  )
  renderPage()

  expect(await screen.findByText('Nexus cannot open its database: Unable to connect to any of the specified MySQL hosts.')).toBeInTheDocument()
  expect(screen.getByLabelText('Host')).toHaveValue('db.test')
  expect(screen.getByLabelText('Port')).toHaveValue(3307)
  expect(screen.getByLabelText('Password')).toHaveValue('')
})

it('has someone locked out ask for a code, then make an admin', async () => {
  jest.mocked(getInstall).mockResolvedValueOnce(view({ installed: true, stage: 'Done', local: false, allowed: false }))
  jest.mocked(getInstall).mockResolvedValue(view({ installed: true, stage: 'Done', local: false, allowed: true }))
  jest.mocked(askForInstallCode).mockResolvedValue(null)
  jest.mocked(installAdmin).mockResolvedValue(founder)
  renderPage()

  await userEvent.click(await screen.findByRole('button', { name: 'Make an admin' }))
  await userEvent.click(screen.getByRole('button', { name: 'Write a code to the log' }))
  expect(await screen.findByText('The service wrote the code to its log.')).toBeInTheDocument()
  await userEvent.type(screen.getByLabelText('Install code'), 'ABCD-EFGH-JKMN')
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }))
  await userEvent.type(await screen.findByLabelText('Email'), 'founder@acme.test')
  await userEvent.type(screen.getByLabelText('Password'), 'correct horse battery')
  await userEvent.type(screen.getByLabelText('The same password again'), 'correct horse battery')
  await userEvent.click(screen.getByRole('button', { name: 'Make the admin and sign in' }))

  expect(askForInstallCode).toHaveBeenCalled()
  expect(installAdmin).toHaveBeenCalledWith('ABCD-EFGH-JKMN', 'founder@acme.test', 'correct horse battery')
  expect(navigate).toHaveBeenCalledWith('/')
})
