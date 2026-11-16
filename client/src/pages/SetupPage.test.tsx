import { render, screen } from '@testing-library/react'
import { type ReactNode, useState } from 'react'
import userEvent from '@testing-library/user-event'
import { ApiError, getSetup, type Me, setUp } from '../api'
import { SessionContext, SIGNED_OUT } from '../session'
import { SetupPage } from './SetupPage'

jest.mock('react-router', () => jest.requireActual('../../jest/router').reactRouterMock())
const { navigate } = jest.requireActual('../../jest/router')

jest.mock('../api', () => ({ ...jest.requireActual('../api'), getSetup: jest.fn(), setUp: jest.fn() }))

const founder: Me = { signedIn: true, id: 1, email: 'founder@acme.test', isAdmin: true, hasPassword: true }

// A session that changes when the page sets it, as the app's does, so the page re-renders signed in.
function Session({ onSet, children }: { onSet: (me: Me) => void; children: ReactNode }) {
  const [me, setMe] = useState<Me>(SIGNED_OUT)
  const set = (next: Me) => {
    onSet(next)
    setMe(next)
  }
  return <SessionContext.Provider value={{ me, setMe: set }}>{children}</SessionContext.Provider>
}

function renderPage(setMe = jest.fn()) {
  render(
    <Session onSet={setMe}>
      <SetupPage />
    </Session>,
  )
  return setMe
}

beforeEach(() => {
  jest.clearAllMocks()
  localStorage.clear()
})

it('makes the first admin from the setup code and signs them in', async () => {
  jest.mocked(getSetup).mockResolvedValue({ needed: true })
  jest.mocked(setUp).mockResolvedValue(founder)
  const setMe = renderPage()

  await userEvent.type(screen.getByLabelText('Setup code'), 'K7QF-2MXD-9RWA')
  await userEvent.type(screen.getByLabelText('Your email'), 'founder@acme.test')
  await userEvent.type(screen.getByLabelText('Password'), 'correct horse battery')
  await userEvent.type(screen.getByLabelText('The same password again'), 'correct horse battery')
  await userEvent.click(await screen.findByRole('button', { name: 'Set up and sign in' }))

  expect(setUp).toHaveBeenCalledWith('K7QF-2MXD-9RWA', 'founder@acme.test', 'correct horse battery')
  expect(setMe).toHaveBeenCalledWith(founder)
  expect(navigate).toHaveBeenCalledWith('/')
})

it('says a wrong code is wrong', async () => {
  jest.mocked(getSetup).mockResolvedValue({ needed: true })
  jest.mocked(setUp).mockRejectedValue(new ApiError(400, "That is not this instance's setup code."))
  renderPage()

  await userEvent.type(screen.getByLabelText('Setup code'), 'WRONG')
  await userEvent.type(screen.getByLabelText('Your email'), 'founder@acme.test')
  await userEvent.type(screen.getByLabelText('Password'), 'correct horse battery')
  await userEvent.type(screen.getByLabelText('The same password again'), 'correct horse battery')
  await userEvent.click(await screen.findByRole('button', { name: 'Set up and sign in' }))

  expect(await screen.findByText("That is not this instance's setup code.")).toBeInTheDocument()
})

it('sends a visitor to sign in once the instance is set up', async () => {
  jest.mocked(getSetup).mockResolvedValue({ needed: false })
  renderPage()

  await userEvent.click(await screen.findByRole('button', { name: 'Sign in' }))

  expect(navigate).toHaveBeenCalledWith('/sign-in')
})
