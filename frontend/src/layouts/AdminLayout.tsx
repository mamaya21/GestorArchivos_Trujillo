import { NavLink, Outlet } from 'react-router-dom'

export default function AdminLayout() {
  return (
    <div className="admin-layout">
      <aside className="admin-sidebar">
        <div className="admin-sidebar-title">Administración</div>

        <nav className="admin-submenu">
          <NavLink
            to="/admin/users"
            className={({ isActive }) =>
              isActive ? 'admin-submenu-active' : ''
            }
          >
            Usuarios
          </NavLink>

          <NavLink
            to="/admin/groups"
            className={({ isActive }) =>
              isActive ? 'admin-submenu-active' : ''
            }
          >
            Grupos
          </NavLink>

          <NavLink
            to="/admin/permissions"
            className={({ isActive }) =>
              isActive ? 'admin-submenu-active' : ''
            }
          >
            Permisos
          </NavLink>

          <NavLink
            to="/admin/audit"
            className={({ isActive }) =>
              isActive ? 'admin-submenu-active' : ''
            }
          >
            Auditoría
          </NavLink>
        </nav>
      </aside>

      <section className="admin-content">
        <Outlet />
      </section>
    </div>
  )
}