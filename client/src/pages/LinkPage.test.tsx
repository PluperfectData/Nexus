import { render, screen } from '@testing-library/react'
import { StrictMode } from 'react'
import { ApiError, redeemLink, type Me } from '../api'
import { SessionContext, SIGNED_OUT } from '../session'
import { LinkPage } from './LinkPage'

jest.mock('react-router', () => jest.requireActual('../../jest/router').reactRouterMock())
const { navigate } = jest.requireActual('../../jest/router')

jest.mock('../api', () => ({ ...jest.requireActual('../api'), redeemLink: jest.fn() }))

const rowan: Me = { signedIn: true, id: 1, email: 'rowan@acme.test', isAdmin: false, hasPassword: false }

function renderPage(setMe = jest.fn()) {
  render(
    <StrictMode>
      <SessionContext.Provider value={{ me: SIGNED_OUT, setMe }}>
        <LinkPage />
      </SessionContext.Provider>
    </StrictMode>,
  )
  return setMe
}

beforeEach(() => {
  jest.clearAllMocks()
  localStorage.clear()
  window.location.hash = '#the-token'
})

it('redeems the token after the # once, even in strict mode, and opens the workspaces', async () => {
  jest.mocked(redeemLink).mockResolvedValue(rowan)
  const setMe = renderPage()

  await screen.findByText('Checking your link…')
  await new Promise((resolve) => setTimeout(resolve, 0))

  expect(redeemLink).toHaveBeenCalledTimes(1)
  expect(redeemLink).toHaveBeenCalledWith('the-token', false)
  expect(setMe).toHaveBeenCalledWith(rowan)
  expect(navigate).toHaveBeenCalledWith('/', { replace: true })
})

it('says a used link is used, and points back to sign in', async () => {
  jest.mocked(redeemLink).mockRejectedValue(new ApiError(400, 'This link has expired or was already used. Ask for a new one.'))
  renderPage()

  expect(await screen.findByText('This link has expired or was already used. Ask for a new one.')).toBeInTheDocument()
  expect(screen.getByRole('link', { name: 'Back to sign in' })).toHaveAttribute('href', '/sign-in')
})
