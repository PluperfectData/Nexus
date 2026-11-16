import { ApiError, listWorkspaces, signIn, tableName } from './api'

const mockFetch = jest.fn()

jest.mock('@pluperfect/jwt', () => ({ createAuthClient: () => (path: string, init: RequestInit) => mockFetch(path, init) }))

// jsdom has no Response, so this is one in shape.
const answer = (status: number, body: unknown) => ({
  ok: status < 400,
  status,
  headers: { get: (name: string) => (name === 'content-type' ? 'application/json' : null) },
  json: async () => body,
})

beforeEach(() => mockFetch.mockReset())

it('sends JSON through the session client and reads JSON back', async () => {
  mockFetch.mockResolvedValue(answer(200, { signedIn: true, email: 'rowan@acme.test' }))

  const me = await signIn('rowan@acme.test', 'secret', true)

  expect(me.email).toBe('rowan@acme.test')
  const [path, init] = mockFetch.mock.calls[0]
  expect(path).toBe('/auth/sign-in')
  expect(init.method).toBe('POST')
  expect(JSON.parse(init.body)).toEqual({ email: 'rowan@acme.test', password: 'secret', remember: true })
})

it("refuses with the service's own words", async () => {
  mockFetch.mockResolvedValue(answer(403, { title: 'Forbidden', detail: 'Only admins manage users.' }))

  const refused = listWorkspaces()

  await expect(refused).rejects.toEqual(new ApiError(403, 'Only admins manage users.'))
})

it('turns a stream name into a table name Scry takes', () => {
  expect(tableName('shop.Orders')).toBe('shop_orders')
  expect(tableName('2026 sales')).toBe('t_2026_sales')
  expect(tableName('x'.repeat(80))).toHaveLength(63)
})
