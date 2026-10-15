import { useState, type FormEvent } from 'react'
import { Navigate, useNavigate } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'

export default function LoginPage() {
  const { user, login } = useAuth()
  const navigate = useNavigate()

  const [userName, setUserName] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [submitting, setSubmitting] = useState(false)

  if (user) {
    return <Navigate to="/" replace />
  }

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()

    setError('')
    setSubmitting(true)

    try {
      await login(userName, password)
      navigate('/')
    } catch {
      setError('Usuario o contraseña incorrectos.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <main className="login-page">
      <form className="login-card" onSubmit={handleSubmit}>
        <h1>Gestor de Archivos</h1>

        <label>
          Usuario
          <input
            value={userName}
            onChange={(event) => setUserName(event.target.value)}
            autoComplete="username"
            required
          />
        </label>

        <label>
          Contraseña
          <input
            type="password"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            autoComplete="current-password"
            required
          />
        </label>

        {error && <p className="error">{error}</p>}

        <button disabled={submitting}>
          {submitting ? 'Ingresando...' : 'Ingresar'}
        </button>
      </form>
    </main>
  )
}