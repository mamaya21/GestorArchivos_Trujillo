import { useCallback, useEffect, useMemo, useState } from 'react'
import Swal from 'sweetalert2'
import { apiRequest } from '../api/client'

interface AdminUser {
  id: string
  userName: string
  email: string
  nombre: string
  apellido: string
  activo: boolean
  fechaCreacion: string
  ultimoAcceso: string | null
  roles: string[]
}

interface UserForm {
  userName: string
  email: string
  nombre: string
  apellido: string
  password: string
  role: string
}


export default function AdminUsersPage() {
  const [users, setUsers] = useState<AdminUser[]>([])
  const [search, setSearch] = useState('')
  const [loading, setLoading] = useState(true)
  const [expandedUserId, setExpandedUserId] = useState<string | null>(null)

  const loadUsers = useCallback(async () => {
    setLoading(true)

    try {
      const data = await apiRequest<AdminUser[]>('/admin/users')
      setUsers(data)
    } catch (error) {
      await showError(error)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void loadUsers()
  }, [loadUsers])

  const visibleUsers = useMemo(() => {
    const query = search.trim().toLocaleLowerCase('es')

    if (!query) return users

    return users.filter((user) =>
      [
        user.userName,
        user.email,
        user.nombre,
        user.apellido,
        ...user.roles,
      ]
        .join(' ')
        .toLocaleLowerCase('es')
        .includes(query),
    )
  }, [users, search])

  async function createUser() {
    const result = await Swal.fire({
      title: 'Nuevo usuario',
      html: `
        <div class="swal-form">
          <label>Usuario</label>
          <input id="swal-username" class="swal2-input" autocomplete="off">

          <label>Nombre</label>
          <input id="swal-nombre" class="swal2-input" autocomplete="off">

          <label>Apellido</label>
          <input id="swal-apellido" class="swal2-input" autocomplete="off">

          <label>Correo</label>
          <input id="swal-email" type="email" class="swal2-input" autocomplete="off">

          <label>Contraseña</label>
          <input id="swal-password" type="password" class="swal2-input" autocomplete="new-password">

          <label>Rol</label>
          <select id="swal-role" class="swal2-select">
            <option value="Usuario">Usuario</option>
            <option value="Administrador">Administrador</option>
          </select>
        </div>
      `,
      width: 600,
      showCancelButton: true,
      confirmButtonText: 'Crear usuario',
      cancelButtonText: 'Cancelar',
      focusConfirm: false,
      preConfirm: () => {
        const userName = inputValue('swal-username')
        const nombre = inputValue('swal-nombre')
        const apellido = inputValue('swal-apellido')
        const email = inputValue('swal-email')
        const password = inputValue('swal-password')
        const role = inputValue('swal-role')

        if (
          !userName ||
          !nombre ||
          !apellido ||
          !email ||
          !password
        ) {
          Swal.showValidationMessage(
            'Todos los campos son obligatorios.',
          )
          return false
        }

        if (password.length < 12) {
          Swal.showValidationMessage(
            'La contraseña debe tener al menos 12 caracteres.',
          )
          return false
        }

        return {
          userName,
          nombre,
          apellido,
          email,
          password,
          role,
        } satisfies UserForm
      },
    })

    if (!result.isConfirmed || !result.value) return

    try {
      await apiRequest('/admin/users', {
        method: 'POST',
        body: JSON.stringify(result.value),
      })

      await loadUsers()
      await showSuccess('Usuario creado correctamente.')
    } catch (error) {
      await showError(error)
    }
  }

  async function editUser(user: AdminUser) {
    const result = await Swal.fire({
      title: 'Editar usuario',
      html: `
        <div class="swal-form">
          <label>Usuario</label>
          <input
            id="swal-username"
            class="swal2-input"
            value="${escapeHtml(user.userName)}"
            disabled
          >

          <label>Nombre</label>
          <input
            id="swal-nombre"
            class="swal2-input"
            value="${escapeHtml(user.nombre)}"
          >

          <label>Apellido</label>
          <input
            id="swal-apellido"
            class="swal2-input"
            value="${escapeHtml(user.apellido)}"
          >

          <label>Correo</label>
          <input
            id="swal-email"
            type="email"
            class="swal2-input"
            value="${escapeHtml(user.email)}"
          >
        </div>
      `,
      width: 600,
      showCancelButton: true,
      confirmButtonText: 'Guardar cambios',
      cancelButtonText: 'Cancelar',
      focusConfirm: false,
      preConfirm: () => {
        const nombre = inputValue('swal-nombre')
        const apellido = inputValue('swal-apellido')
        const email = inputValue('swal-email')

        if (!nombre || !apellido || !email) {
          Swal.showValidationMessage(
            'Nombre, apellido y correo son obligatorios.',
          )
          return false
        }

        return { nombre, apellido, email }
      },
    })

    if (!result.isConfirmed || !result.value) return

    try {
      await apiRequest(`/admin/users/${user.id}`, {
        method: 'PUT',
        body: JSON.stringify(result.value),
      })

      await loadUsers()
      await showSuccess('Usuario actualizado correctamente.')
    } catch (error) {
      await showError(error)
    }
  }

  async function changeRole(user: AdminUser) {
    const currentRole = user.roles.includes('Administrador')
      ? 'Administrador'
      : 'Usuario'

    const result = await Swal.fire({
      title: 'Cambiar rol',
      text: user.userName,
      input: 'select',
      inputOptions: {
        Usuario: 'Usuario',
        Administrador: 'Administrador',
      },
      inputValue: currentRole,
      showCancelButton: true,
      confirmButtonText: 'Guardar',
      cancelButtonText: 'Cancelar',
      inputValidator: (value) =>
        value ? undefined : 'Selecciona un rol.',
    })

    if (!result.isConfirmed) return

    try {
      await apiRequest(`/admin/users/${user.id}/role`, {
        method: 'PUT',
        body: JSON.stringify({
          role: result.value,
        }),
      })

      await loadUsers()
      await showSuccess('Rol actualizado correctamente.')
    } catch (error) {
      await showError(error)
    }
  }

  async function resetPassword(user: AdminUser) {
    const result = await Swal.fire({
      title: 'Restablecer contraseña',
      text: user.userName,
      input: 'password',
      inputPlaceholder: 'Nueva contraseña',
      inputAttributes: {
        autocomplete: 'new-password',
      },
      showCancelButton: true,
      confirmButtonText: 'Restablecer',
      cancelButtonText: 'Cancelar',
      inputValidator: (value) => {
        if (!value) return 'Ingresa la nueva contraseña.'
        if (value.length < 12) {
          return 'La contraseña debe tener al menos 12 caracteres.'
        }

        return undefined
      },
    })

    if (!result.isConfirmed) return

    try {
      await apiRequest(
        `/admin/users/${user.id}/reset-password`,
        {
          method: 'POST',
          body: JSON.stringify({
            newPassword: result.value,
          }),
        },
      )

      await showSuccess('Contraseña restablecida correctamente.')
    } catch (error) {
      await showError(error)
    }
  }

  async function changeStatus(user: AdminUser) {
    const action = user.activo ? 'desactivar' : 'activar'

    const result = await Swal.fire({
      title: `¿${capitalize(action)} usuario?`,
      text: user.userName,
      icon: 'question',
      showCancelButton: true,
      confirmButtonText: capitalize(action),
      cancelButtonText: 'Cancelar',
    })

    if (!result.isConfirmed) return

    try {
      await apiRequest(`/admin/users/${user.id}/status`, {
        method: 'PUT',
        body: JSON.stringify({
          active: !user.activo,
        }),
      })

      await loadUsers()

      await showSuccess(
        user.activo
          ? 'Usuario desactivado correctamente.'
          : 'Usuario activado correctamente.',
      )
    } catch (error) {
      await showError(error)
    }
  }

  return (
    <div className="admin-page">
      <div className="admin-title">
        <div>
          <h1>Usuarios</h1>
          <p>Administración de usuarios y roles</p>
        </div>

        <button type="button" onClick={() => void createUser()}>
          + Nuevo usuario
        </button>
      </div>

      <div className="explorer-tools">
        <input
          className="search-box"
          type="search"
          placeholder="Buscar usuario..."
          value={search}
          onChange={(event) => setSearch(event.target.value)}
        />

        <strong>{users.length} usuario(s)</strong>
      </div>

      <div className="admin-table-wrapper">
        <table className="admin-table">
          <thead>
            <tr>
              <th>Usuario</th>
              <th>Nombre</th>
              <th>Correo</th>
              <th>Rol</th>
              <th>Estado</th>
              <th>Último acceso</th>
              <th>Acciones</th>
            </tr>
          </thead>

          <tbody>
            {loading ? (
              <tr>
                <td colSpan={7}>Cargando...</td>
              </tr>
            ) : (
              visibleUsers.map((user) => (
                <tr
                    key={user.id}
                    className={
                        expandedUserId === user.id
                        ? 'admin-user-expanded'
                        : ''
                    }
                    onClick={() =>
                        setExpandedUserId((current) =>
                        current === user.id ? null : user.id,
                        )
                    }
                    >
                  <td>
                    <strong>{user.userName}</strong>
                  </td>

                  <td>
                    {user.nombre} {user.apellido}
                  </td>

                  <td>{user.email}</td>

                  <td>{user.roles.join(', ') || 'Sin rol'}</td>

                  <td>
                    <span
                      className={
                        user.activo
                          ? 'status-active'
                          : 'status-inactive'
                      }
                    >
                      {user.activo ? 'Activo' : 'Inactivo'}
                    </span>
                  </td>

                  <td>
                    {user.ultimoAcceso
                      ? formatDate(user.ultimoAcceso)
                      : 'Nunca'}
                  </td>

                  <td>
                    <div
                        className="row-actions"
                        onClick={(event) => event.stopPropagation()}
                        >
                      <button
                        type="button"
                        onClick={() => void editUser(user)}
                      >
                        Editar
                      </button>

                      <button
                        type="button"
                        onClick={() => void changeRole(user)}
                      >
                        Rol
                      </button>

                      <button
                        type="button"
                        onClick={() => void resetPassword(user)}
                      >
                        Contraseña
                      </button>

                      <button
                        type="button"
                        onClick={() => void changeStatus(user)}
                      >
                        {user.activo ? 'Desactivar' : 'Activar'}
                      </button>
                    </div>
                  </td>
                </tr>
              ))
            )}

            {!loading && visibleUsers.length === 0 && (
              <tr>
                <td colSpan={7}>
                  No se encontraron usuarios.
                </td>
              </tr>
            )}
          </tbody>
        </table>
      </div>
    </div>
  )
}

function inputValue(id: string) {
  const element = document.getElementById(
    id,
  ) as HTMLInputElement | HTMLSelectElement | null

  return element?.value.trim() ?? ''
}

async function showSuccess(message: string) {
  await Swal.fire({
    icon: 'success',
    title: 'Correcto',
    text: message,
    timer: 1600,
    showConfirmButton: false,
  })
}

async function showError(error: unknown) {
  await Swal.fire({
    icon: 'error',
    title: 'Error',
    text: getError(error),
  })
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat('es-PE', {
    dateStyle: 'short',
    timeStyle: 'short',
  }).format(new Date(value))
}

function capitalize(value: string) {
  return value.charAt(0).toUpperCase() + value.slice(1)
}

function escapeHtml(value: string) {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#039;')
}

function getError(error: unknown) {
  return error instanceof Error
    ? error.message
    : 'Ocurrió un error inesperado.'
}