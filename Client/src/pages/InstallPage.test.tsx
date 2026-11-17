import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import {
  askForInstallCode,
  getInstall,
  getSettings,
  installAddress,
  installAdmin,
  installDatabase,
  installMail,
  installMailOff,
  installTestMail,
  type InstallView,
  type MailSettings,
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
  installMail: jest.fn(),
  installMailOff: jest.fn(),
  installTestMail: jest.fn(),
  askForInstallCode: jest.fn(),
  getSettings: jest.fn(),
}))

const mailOff: MailSettings = {
  transport: 'None',
  tokenSet: false,
  tokenConfigured: false,
  from: null,
  fromConfigured: false,
  fromName: 'Nexus',
  fromNameConfigured: false,
  folder: null,
}

// Development's mail: written to files, as configuration says.
const mailToFiles: MailSettings = { ...mailOff, transport: 'Files', folder: 'D:\\nexus\\data\\mail' }

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
  mailSettings: null,
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

it('takes a page on this machine through the database, the address, mail, and the first admin, then the callback', async () => {
  jest.mocked(getInstall).mockResolvedValueOnce(view({})).mockResolvedValue(view({ installed: true, stage: 'Done' }))
  jest.mocked(installDatabase).mockResolvedValue(view({ stage: 'Address' }))
  jest.mocked(installAddress).mockResolvedValue(view({ stage: 'Admin', mailSettings: mailOff }))
  jest.mocked(installAdmin).mockResolvedValue(founder)
  jest.mocked(getSettings).mockResolvedValue(settings)
  const setMe = renderPage()

  await userEvent.type(await screen.findByLabelText('Password'), 'database-password')
  await userEvent.click(screen.getByRole('button', { name: 'Test and save' }))
  await userEvent.click(await screen.findByRole('button', { name: 'Save address' }))
  expect(await screen.findByText('Step 3 of 5: Mail')).toBeInTheDocument()
  await userEvent.click(screen.getByRole('button', { name: 'Continue without mail' }))
  expect(await screen.findByText('Step 4 of 5: First admin')).toBeInTheDocument()
  expect(screen.getByLabelText('Password')).toBeRequired()
  await userEvent.type(screen.getByLabelText('Email'), 'founder@acme.test')
  await userEvent.type(screen.getByLabelText('Password'), 'correct horse battery')
  await userEvent.type(screen.getByLabelText('The same password again'), 'correct horse battery')
  await userEvent.click(screen.getByRole('button', { name: 'Make the admin and sign in' }))

  expect(installDatabase).toHaveBeenCalledWith(null, { host: 'localhost', port: 3306, name: 'nexus', user: 'nexus', password: 'database-password' })
  expect(installAddress).toHaveBeenCalledWith(null, 'http://localhost')
  expect(installAdmin).toHaveBeenCalledWith(null, 'founder@acme.test', 'correct horse battery')
  expect(setMe).toHaveBeenCalledWith(founder)
  expect(await screen.findByText('Step 5 of 5: Callback')).toBeInTheDocument()
  expect(screen.queryByLabelText('Postmark server token')).not.toBeInTheDocument()
  expect(screen.getByLabelText('Callback address')).toHaveValue('https://127.0.0.1:6164')
  await userEvent.click(screen.getByRole('button', { name: 'Done' }))
  expect(navigate).toHaveBeenCalledWith('/')
})

it('makes the first admin with no password once mail works, to sign in by mailed links', async () => {
  jest.mocked(getInstall).mockResolvedValue(view({ stage: 'Admin', mail: true, mailSettings: mailToFiles }))
  jest.mocked(installAdmin).mockResolvedValue({ ...founder, hasPassword: false })
  jest.mocked(getSettings).mockResolvedValue(settings)
  renderPage()

  expect(await screen.findByText(/writes mail to files in D:\\nexus\\data\\mail/)).toBeInTheDocument()
  await userEvent.click(screen.getByRole('button', { name: 'Continue' }))
  expect(screen.getByLabelText('Password (optional)')).not.toBeRequired()
  expect(screen.getByLabelText('The same password again (optional)')).not.toBeRequired()
  expect(screen.getByText(/leave both fields blank to sign in with mailed links/)).toBeInTheDocument()
  await userEvent.type(screen.getByLabelText('Email'), 'founder@acme.test')
  await userEvent.click(screen.getByRole('button', { name: 'Make the admin and sign in' }))

  expect(installAdmin).toHaveBeenCalledWith(null, 'founder@acme.test', '')
  expect(await screen.findByText('Step 5 of 5: Callback')).toBeInTheDocument()
})

it('goes back to mail files after testing Postmark in a debug build', async () => {
  const postmark: MailSettings = { ...mailToFiles, transport: 'Postmark', tokenSet: true, from: 'test@acme.test' }
  jest.mocked(getInstall).mockResolvedValue(view({ stage: 'Admin', mail: true, mailSettings: postmark }))
  jest.mocked(installMailOff).mockResolvedValue(view({ stage: 'Admin', mail: true, mailSettings: mailToFiles }))
  jest.spyOn(window, 'confirm').mockReturnValue(true)
  renderPage()

  expect(await screen.findByText(/Mail goes through Postmark/)).toBeInTheDocument()
  await userEvent.click(screen.getByRole('button', { name: 'Use mail files again' }))

  expect(window.confirm).toHaveBeenCalledWith('Stop sending through Postmark? Mail goes to files in D:\\nexus\\data\\mail again.')
  expect(installMailOff).toHaveBeenCalledWith(null)
  expect(await screen.findByText('Mail goes to files again.')).toBeInTheDocument()
  expect(screen.getByText(/This debug build writes mail to files/)).toBeInTheDocument()
})

it('saves mail in the installer, and sends a test mail to the address given', async () => {
  jest.mocked(getInstall).mockResolvedValue(view({ stage: 'Admin', mailSettings: mailOff }))
  jest.mocked(installMail).mockResolvedValue(view({ stage: 'Admin', mail: true, mailSettings: { ...mailOff, transport: 'Postmark', tokenSet: true, from: 'nexus@acme.test' } }))
  jest.mocked(installTestMail).mockResolvedValue({ sentTo: 'founder@acme.test', transport: 'Postmark', folder: null })
  renderPage()

  await userEvent.type(await screen.findByLabelText('Postmark server token'), 'postmark-token')
  await userEvent.type(screen.getByLabelText('From address'), 'nexus@acme.test')
  await userEvent.click(screen.getByRole('button', { name: 'Save mail' }))
  await userEvent.type(await screen.findByLabelText('Send a test mail to'), 'founder@acme.test')
  await userEvent.click(screen.getByRole('button', { name: 'Send a test mail' }))

  expect(installMail).toHaveBeenCalledWith(null, 'postmark-token', 'nexus@acme.test', 'Nexus')
  expect(installTestMail).toHaveBeenCalledWith(null, 'founder@acme.test')
  expect(await screen.findByText('Sent a test mail to founder@acme.test.')).toBeInTheDocument()
  expect(screen.getByRole('button', { name: 'Continue' })).toBeInTheDocument()
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
