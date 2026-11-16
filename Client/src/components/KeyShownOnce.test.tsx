import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { KeyShownOnce } from './KeyShownOnce'

const writeText = jest.fn()

beforeEach(() => {
  writeText.mockReset()
  Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
})

it('copies the whole key in one click', async () => {
  writeText.mockResolvedValue(undefined)
  render(<KeyShownOnce label="Your key" value="nexus_the-whole-key" note="Shown once." onDone={jest.fn()} />)

  await userEvent.click(screen.getByRole('button', { name: 'Copy key' }))

  expect(writeText).toHaveBeenCalledWith('nexus_the-whole-key')
  expect(screen.getByRole('button', { name: 'Copied' })).toBeInTheDocument()
})

it('says to copy it by hand when the browser refuses', async () => {
  writeText.mockRejectedValue(new Error('Not allowed'))
  render(<KeyShownOnce label="Your key" value="nexus_the-whole-key" note="Shown once." onDone={jest.fn()} />)

  await userEvent.click(screen.getByRole('button', { name: 'Copy key' }))

  expect(await screen.findByText('The key could not be copied. Select it and copy it yourself.')).toBeInTheDocument()
  expect(screen.getByLabelText('Your key')).toHaveValue('nexus_the-whole-key')
})
