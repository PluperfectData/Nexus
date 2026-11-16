import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { setPassword, type Me } from '../api'
import { SessionContext } from '../session'
import { AccountPage } from './AccountPage'

jest.mock('../api', () => ({ ...jest.requireActual('../api'), setPassword: jest.fn() }))

const rowan: Me = { signedIn: true, id: 1, email: 'rowan@acme.test', isAdmin: false, hasPassword: false }

beforeEach(() => jest.clearAllMocks())

it('sets a password once both entries match', async () => {
  jest.mocked(setPassword).mockResolvedValue({ ...rowan, hasPassword: true })
  const setMe = jest.fn()
  render(
    <SessionContext.Provider value={{ me: rowan, setMe }}>
      <AccountPage />
    </SessionContext.Provider>,
  )
  expect(screen.getByRole('heading', { name: 'Set a password' })).toBeInTheDocument()

  await userEvent.type(screen.getByLabelText('New password'), 'correct horse battery')
  await userEvent.type(screen.getByLabelText('The same password again'), 'correct horse batterz')
  await userEvent.click(screen.getByRole('button', { name: 'Save password' }))
  expect(screen.getByText('The two passwords differ.')).toBeInTheDocument()
  expect(setPassword).not.toHaveBeenCalled()

  await userEvent.clear(screen.getByLabelText('The same password again'))
  await userEvent.type(screen.getByLabelText('The same password again'), 'correct horse battery')
  await userEvent.click(screen.getByRole('button', { name: 'Save password' }))

  expect(setPassword).toHaveBeenCalledWith('correct horse battery')
  expect(await screen.findByText('Your password is set.')).toBeInTheDocument()
  expect(setMe).toHaveBeenCalledWith({ ...rowan, hasPassword: true })
})
