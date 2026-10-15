import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useState,
  type ReactNode,
} from 'react'
import { apiRequest } from '../api/client'
import type { CurrentUser } from '../types/api'

interface AuthContextValue {
  user: CurrentUser | null
  loading: boolean
  login: (userName: string, password: string) => Promise<void>
  logout: () => Promise<void>
}

const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<CurrentUser | null>(null)
  const [loading, setLoading] = useState(true)

  const loadUser = useCallback(async () => {
    try {
      const currentUser = await apiRequest<CurrentUser>('/auth/me')
      setUser(currentUser)
    } catch {
      setUser(null)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void loadUser()
  }, [loadUser])

  async function login(userName: string, password: string) {
    await apiRequest<void>('/auth/login', {
      method: 'POST',
      body: JSON.stringify({ userName, password }),
    })

    await loadUser()
  }

  async function logout() {
    await apiRequest<void>('/auth/logout', {
      method: 'POST',
    })

    setUser(null)
  }

  return (
    <AuthContext.Provider value={{ user, loading, login, logout }}>
      {children}
    </AuthContext.Provider>
  )
}

export function useAuth() {
  const context = useContext(AuthContext)

  if (!context) {
    throw new Error('useAuth debe utilizarse dentro de AuthProvider.')
  }

  return context
}