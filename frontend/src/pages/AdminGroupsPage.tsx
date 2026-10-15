import { useCallback, useEffect, useMemo, useState } from 'react'
import Swal from 'sweetalert2'
import { apiRequest } from '../api/client'

interface Group {
  id: string
  nombre: string
  descripcion: string | null
  activo: boolean
  fechaCreacion: string
  cantidadUsuarios: number
}

interface AdminUser {
  id: string
  userName: string
  email: string
  nombre: string
  apellido: string
  activo: boolean
}

export default function AdminGroupsPage() {
  const [groups, setGroups] = useState<Group[]>([])
  const [search, setSearch] = useState('')
  const [loading, setLoading] = useState(true)
  const [expandedGroupId, setExpandedGroupId] =
    useState<string | null>(null)

  const loadGroups = useCallback(async () => {
    setLoading(true)

    try {
      const data = await apiRequest<Group[]>('/admin/groups')
      setGroups(data)
    } catch (error) {
      await showError(error)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void loadGroups()
  }, [loadGroups])

  const visibleGroups = useMemo(() => {
    const query = search.trim().toLocaleLowerCase('es')

    if (!query) return groups

    return groups.filter((group) =>
      [
        group.nombre,
        group.descripcion ?? '',
        group.activo ? 'activo' : 'inactivo',
      ]
        .join(' ')
        .toLocaleLowerCase('es')
        .includes(query),
    )
  }, [groups, search])

  async function createGroup() {
    const result = await Swal.fire({
      title: 'Nuevo grupo',
      html: `
        <div class="swal-form">
          <label>Nombre</label>
          <input
            id="swal-group-name"
            class="swal2-input"
            maxlength="150"
            autocomplete="off"
          >

          <label>Descripción</label>
          <input
            id="swal-group-description"
            class="swal2-input"
            autocomplete="off"
          >
        </div>
      `,
      width: 600,
      showCancelButton: true,
      confirmButtonText: 'Crear grupo',
      cancelButtonText: 'Cancelar',
      focusConfirm: false,
      preConfirm: () => {
        const name = inputValue('swal-group-name')
        const description = inputValue(
          'swal-group-description',
        )

        if (!name) {
          Swal.showValidationMessage(
            'Ingresa el nombre del grupo.',
          )
          return false
        }

        return {
          name,
          description: description || null,
        }
      },
    })

    if (!result.isConfirmed || !result.value) return

    try {
      await apiRequest('/admin/groups', {
        method: 'POST',
        body: JSON.stringify(result.value),
      })

      await loadGroups()
      await showSuccess('Grupo creado correctamente.')
    } catch (error) {
      await showError(error)
    }
  }

  async function editGroup(group: Group) {
    const result = await Swal.fire({
      title: 'Editar grupo',
      html: `
        <div class="swal-form">
          <label>Nombre</label>
          <input
            id="swal-group-name"
            class="swal2-input"
            maxlength="150"
            value="${escapeHtml(group.nombre)}"
          >

          <label>Descripción</label>
          <input
            id="swal-group-description"
            class="swal2-input"
            value="${escapeHtml(group.descripcion ?? '')}"
          >
        </div>
      `,
      width: 600,
      showCancelButton: true,
      confirmButtonText: 'Guardar cambios',
      cancelButtonText: 'Cancelar',
      focusConfirm: false,
      preConfirm: () => {
        const name = inputValue('swal-group-name')
        const description = inputValue(
          'swal-group-description',
        )

        if (!name) {
          Swal.showValidationMessage(
            'Ingresa el nombre del grupo.',
          )
          return false
        }

        return {
          name,
          description: description || null,
          active: group.activo,
        }
      },
    })

    if (!result.isConfirmed || !result.value) return

    try {
      await apiRequest(`/admin/groups/${group.id}`, {
        method: 'PUT',
        body: JSON.stringify(result.value),
      })

      await loadGroups()
      await showSuccess('Grupo actualizado correctamente.')
    } catch (error) {
      await showError(error)
    }
  }

  async function changeStatus(group: Group) {
    const activate = !group.activo

    const confirmation = await Swal.fire({
      icon: 'question',
      title: activate
        ? '¿Activar grupo?'
        : '¿Desactivar grupo?',
      text: group.nombre,
      showCancelButton: true,
      confirmButtonText: activate
        ? 'Activar'
        : 'Desactivar',
      cancelButtonText: 'Cancelar',
    })

    if (!confirmation.isConfirmed) return

    try {
      await apiRequest(`/admin/groups/${group.id}`, {
        method: 'PUT',
        body: JSON.stringify({
          name: group.nombre,
          description: group.descripcion,
          active: activate,
        }),
      })

      await loadGroups()

      await showSuccess(
        activate
          ? 'Grupo activado correctamente.'
          : 'Grupo desactivado correctamente.',
      )
    } catch (error) {
      await showError(error)
    }
  }

  async function manageMembers(group: Group) {
    try {
      const [allUsers, members] = await Promise.all([
        apiRequest<AdminUser[]>('/admin/users'),
        apiRequest<AdminUser[]>(
          `/admin/groups/${group.id}/users`,
        ),
      ])

      const activeUsers = allUsers.filter(
        (user) => user.activo,
      )

      const memberIds = new Set(
        members.map((user) => user.id),
      )

      const html = `
        <div class="group-members-list">
          ${
            activeUsers.length === 0
              ? '<p>No existen usuarios activos.</p>'
              : activeUsers
                  .map(
                    (user) => `
                      <label class="group-member-option">
                        <input
                          type="checkbox"
                          value="${user.id}"
                          ${
                            memberIds.has(user.id)
                              ? 'checked'
                              : ''
                          }
                        >
                        <span>
                          <strong>
                            ${escapeHtml(user.userName)}
                          </strong>
                          <small>
                            ${escapeHtml(
                              `${user.nombre} ${user.apellido}`,
                            )}
                          </small>
                        </span>
                      </label>
                    `,
                  )
                  .join('')
          }
        </div>
      `

      const result = await Swal.fire({
        title: `Miembros: ${group.nombre}`,
        html,
        width: 620,
        showCancelButton: true,
        confirmButtonText: 'Guardar miembros',
        cancelButtonText: 'Cancelar',
        focusConfirm: false,
        preConfirm: () => {
          const selected = Array.from(
            document.querySelectorAll<HTMLInputElement>(
              '.group-member-option input:checked',
            ),
          ).map((input) => input.value)

          return selected
        },
      })

      if (!result.isConfirmed || !result.value) return

      const selectedIds = new Set(result.value)

      const usersToAdd = activeUsers.filter(
        (user) =>
          selectedIds.has(user.id) &&
          !memberIds.has(user.id),
      )

      const usersToRemove = members.filter(
        (user) => !selectedIds.has(user.id),
      )

      for (const user of usersToAdd) {
        await apiRequest(
          `/admin/groups/${group.id}/users/${user.id}`,
          {
            method: 'POST',
          },
        )
      }

      for (const user of usersToRemove) {
        await apiRequest(
          `/admin/groups/${group.id}/users/${user.id}`,
          {
            method: 'DELETE',
          },
        )
      }

      await loadGroups()
      await showSuccess('Miembros actualizados correctamente.')
    } catch (error) {
      await showError(error)
    }
  }

  return (
    <div className="admin-page">
      <div className="admin-title">
        <div>
          <h1>Grupos</h1>
          <p>
            Administración de grupos y sus miembros
          </p>
        </div>

        <button
          type="button"
          onClick={() => void createGroup()}
        >
          + Nuevo grupo
        </button>
      </div>

      <div className="explorer-tools">
        <input
          className="search-box"
          type="search"
          placeholder="Buscar grupo..."
          value={search}
          onChange={(event) =>
            setSearch(event.target.value)
          }
        />

        <strong>{groups.length} grupo(s)</strong>
      </div>

      <div className="admin-table-wrapper">
        <table className="admin-table admin-groups-table">
          <thead>
            <tr>
              <th>Grupo</th>
              <th>Descripción</th>
              <th>Miembros</th>
              <th>Estado</th>
              <th>Creación</th>
              <th>Acciones</th>
            </tr>
          </thead>

          <tbody>
            {loading ? (
              <tr>
                <td colSpan={6}>Cargando...</td>
              </tr>
            ) : (
              visibleGroups.map((group) => (
                <tr
                  key={group.id}
                  className={
                    expandedGroupId === group.id
                      ? 'admin-user-expanded'
                      : ''
                  }
                  onClick={() =>
                    setExpandedGroupId((current) =>
                      current === group.id
                        ? null
                        : group.id,
                    )
                  }
                >
                  <td>
                    <strong>{group.nombre}</strong>
                  </td>

                  <td>
                    {group.descripcion || 'Sin descripción'}
                  </td>

                  <td>{group.cantidadUsuarios}</td>

                  <td>
                    <span
                      className={
                        group.activo
                          ? 'status-active'
                          : 'status-inactive'
                      }
                    >
                      {group.activo
                        ? 'Activo'
                        : 'Inactivo'}
                    </span>
                  </td>

                  <td>{formatDate(group.fechaCreacion)}</td>

                  <td>
                    <div
                      className="row-actions"
                      onClick={(event) =>
                        event.stopPropagation()
                      }
                    >
                      <button
                        type="button"
                        onClick={() =>
                          void editGroup(group)
                        }
                      >
                        Editar
                      </button>

                      <button
                        type="button"
                        disabled={!group.activo}
                        onClick={() =>
                          void manageMembers(group)
                        }
                      >
                        Miembros
                      </button>

                      <button
                        type="button"
                        onClick={() =>
                          void changeStatus(group)
                        }
                      >
                        {group.activo
                          ? 'Desactivar'
                          : 'Activar'}
                      </button>
                    </div>
                  </td>
                </tr>
              ))
            )}

            {!loading && visibleGroups.length === 0 && (
              <tr>
                <td colSpan={6}>
                  No se encontraron grupos.
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
  ) as HTMLInputElement | null

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