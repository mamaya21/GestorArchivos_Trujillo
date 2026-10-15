import { NavLink, Outlet } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'

export default function AppLayout() {
  const { user, logout } = useAuth()

  return (
    <div className="app">
      <header className="app-header">
        <strong>Gestor de Archivos</strong>

        <nav className="main-nav">
          <NavLink
            to="/"
            end
            className={({ isActive }) =>
              isActive ? 'nav-active' : ''
            }
          >
            Archivos
          </NavLink>

          <NavLink
            to="/trash"
            className={({ isActive }) =>
              isActive ? 'nav-active' : ''
            }
          >
            Papelera
          </NavLink>

          <NavLink
            to="/admin/users"
            className={({ isActive }) =>
              isActive ? 'nav-active' : ''
            }
          >
            Administración
          </NavLink>
        </nav>

        <div>
          <span>{user?.nombre || user?.userName}</span>

          <button
            type="button"
            onClick={() => void logout()}
          >
            Cerrar sesión
          </button>
        </div>
      </header>

      <main className="app-content">
        <Outlet />
      </main>
    </div>
  )
}