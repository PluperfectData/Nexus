import { createContext, useContext } from 'react'

export type Theme = 'light' | 'dark'

export const THEME_STORAGE_KEY = 'nexus-theme'

/** Light unless this browser chose dark before. */
export function storedTheme(): Theme {
  try {
    return localStorage.getItem(THEME_STORAGE_KEY) === 'dark' ? 'dark' : 'light'
  } catch {
    return 'light'
  }
}

export const ThemeContext = createContext<{ theme: Theme; toggle: () => void }>({ theme: 'light', toggle: () => {} })

export const useTheme = () => useContext(ThemeContext)
