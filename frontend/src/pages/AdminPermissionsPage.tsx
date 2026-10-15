import { useCallback, useEffect, useMemo, useState } from 'react'
import Swal from 'sweetalert2'
import { apiRequest } from '../api/client'

interface Folder {
  id: string
  nombre: string
}

interface AdminUser {
  id: string
  userName: string
  nombre: string
  apellido: string
  activo: boolean
}

interface Group {
  id: string
  nombre: string
  activo: boolean
}

interface UserPermission {
  id: string
  usuarioId: string
  permisos: number
  heredarSubcarpetas: boolean
}

interface GroupPermission {
  id: string
  grupoId: string
  permisos: number
  heredarSubcarpetas: boolean
}

interface PermissionResponse {
  users: UserPermission[]
  groups: GroupPermission[]
}

interface EffectivePermission {
  folderId: string
  userId: string
  permissions: number
  value: number
}

const PERMISSIONS = [
  { value: 1, label: 'Leer' },
  { value: 2, label: 'Crear' },
  { value: 4, label: 'Modificar' },
  { value: 8, label: 'Eliminar' },
  { value: 16, label: 'Administrar' },
]

export default function AdminPermissionsPage() {
  const [folders, setFolders] = useState<Folder[]>([])
  const [users, setUsers] = useState<AdminUser[]>([])
  const [groups, setGroups] = useState<Group[]>([])
  const [folderId, setFolderId] = useState('')
  const [permissions, setPermissions] =
    useState<PermissionResponse>({
      users: [],
      groups: [],
    })
  const [loading, setLoading] = useState(true)

  const loadInitialData = useCallback(async () => {
    setLoading(true)

    try {
      const [folderData, userData, groupData] =
        await Promise.all([
          apiRequest<{
            folders: Folder[]
          }>('/folders'),
          apiRequest<AdminUser[]>('/admin/users'),
          apiRequest<Group[]>('/admin/groups'),
        ])

      setFolders(folderData.folders)
      setUsers(userData)
      setGroups(groupData)
    } catch (error) {
      await showError(error)
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void loadInitialData()
  }, [loadInitialData])

  const loadPermissions = useCallback(
    async (selectedFolderId: string) => {
      if (!selectedFolderId) {
        setPermissions({
          users: [],
          groups: [],
        })
        return
      }

      try {
        const data =
          await apiRequest<PermissionResponse>(
            `/admin/folders/${selectedFolderId}/permissions`,
          )

        setPermissions(data)
      } catch (error) {
        await showError(error)
      }
    },
    [],
  )

  async function selectFolder(id: string) {
    setFolderId(id)
    await loadPermissions(id)
  }

  async function setUserPermission() {
    if (!folderId) return

    const activeUsers = users.filter(
      (user) => user.activo,
    )

    if (activeUsers.length === 0) {
      await showError(
        new Error('No existen usuarios activos.'),
      )
      return
    }

    const result = await permissionDialog(
      'Permiso de usuario',
      activeUsers.map((user) => ({
        id: user.id,
        label: `${user.userName} — ${user.nombre} ${user.apellido}`,
      })),
      'Usuario',
    )

    if (!result) return

    try {
      await apiRequest(
        `/admin/folders/${folderId}/permissions/users/${result.subjectId}`,
        {
          method: 'PUT',
          body: JSON.stringify({
            permissions: result.permissions,
            inheritToSubfolders: result.inherit,
          }),
        },
      )

      await loadPermissions(folderId)
      await showSuccess('Permiso guardado correctamente.')
    } catch (error) {
      await showError(error)
    }
  }

  async function setGroupPermission() {
    if (!folderId) return

    const activeGroups = groups.filter(
      (group) => group.activo,
    )

    if (activeGroups.length === 0) {
      await showError(
        new Error('No existen grupos activos.'),
      )
      return
    }

    const result = await permissionDialog(
      'Permiso de grupo',
      activeGroups.map((group) => ({
        id: group.id,
        label: group.nombre,
      })),
      'Grupo',
    )

    if (!result) return

    try {
      await apiRequest(
        `/admin/folders/${folderId}/permissions/groups/${result.subjectId}`,
        {
          method: 'PUT',
          body: JSON.stringify({
            permissions: result.permissions,
            inheritToSubfolders: result.inherit,
          }),
        },
      )

      await loadPermissions(folderId)
      await showSuccess('Permiso guardado correctamente.')
    } catch (error) {
      await showError(error)
    }
  }

  async function editUserPermission(
    permission: UserPermission,
  ) {
    const user = users.find(
      (item) => item.id === permission.usuarioId,
    )

    if (!user) return

    const result = await permissionDialog(
      `Permisos: ${user.userName}`,
      [
        {
          id: user.id,
          label: `${user.userName} — ${user.nombre} ${user.apellido}`,
        },
      ],
      'Usuario',
      permission.usuarioId,
      permission.permisos,
      permission.heredarSubcarpetas,
      true,
    )

    if (!result) return

    try {
      await apiRequest(
        `/admin/folders/${folderId}/permissions/users/${permission.usuarioId}`,
        {
          method: 'PUT',
          body: JSON.stringify({
            permissions: result.permissions,
            inheritToSubfolders: result.inherit,
          }),
        },
      )

      await loadPermissions(folderId)
      await showSuccess('Permiso actualizado correctamente.')
    } catch (error) {
      await showError(error)
    }
  }

  async function editGroupPermission(
    permission: GroupPermission,
  ) {
    const group = groups.find(
      (item) => item.id === permission.grupoId,
    )

    if (!group) return

    const result = await permissionDialog(
      `Permisos: ${group.nombre}`,
      [{ id: group.id, label: group.nombre }],
      'Grupo',
      permission.grupoId,
      permission.permisos,
      permission.heredarSubcarpetas,
      true,
    )

    if (!result) return

    try {
      await apiRequest(
        `/admin/folders/${folderId}/permissions/groups/${permission.grupoId}`,
        {
          method: 'PUT',
          body: JSON.stringify({
            permissions: result.permissions,
            inheritToSubfolders: result.inherit,
          }),
        },
      )

      await loadPermissions(folderId)
      await showSuccess('Permiso actualizado correctamente.')
    } catch (error) {
      await showError(error)
    }
  }

  async function deleteUserPermission(
    permission: UserPermission,
  ) {
    const user = users.find(
      (item) => item.id === permission.usuarioId,
    )

    const confirmation = await Swal.fire({
      icon: 'warning',
      title: '¿Eliminar permiso?',
      text: user?.userName ?? 'Usuario',
      showCancelButton: true,
      confirmButtonText: 'Eliminar',
      cancelButtonText: 'Cancelar',
    })

    if (!confirmation.isConfirmed) return

    try {
      await apiRequest(
        `/admin/folders/${folderId}/permissions/users/${permission.usuarioId}`,
        { method: 'DELETE' },
      )

      await loadPermissions(folderId)
      await showSuccess('Permiso eliminado correctamente.')
    } catch (error) {
      await showError(error)
    }
  }

  async function deleteGroupPermission(
    permission: GroupPermission,
  ) {
    const group = groups.find(
      (item) => item.id === permission.grupoId,
    )

    const confirmation = await Swal.fire({
      icon: 'warning',
      title: '¿Eliminar permiso?',
      text: group?.nombre ?? 'Grupo',
      showCancelButton: true,
      confirmButtonText: 'Eliminar',
      cancelButtonText: 'Cancelar',
    })

    if (!confirmation.isConfirmed) return

    try {
      await apiRequest(
        `/admin/folders/${folderId}/permissions/groups/${permission.grupoId}`,
        { method: 'DELETE' },
      )

      await loadPermissions(folderId)
      await showSuccess('Permiso eliminado correctamente.')
    } catch (error) {
      await showError(error)
    }
  }

  async function viewEffectivePermission() {
    if (!folderId) return

    const activeUsers = users.filter(
      (user) => user.activo,
    )

    const options = Object.fromEntries(
      activeUsers.map((user) => [
        user.id,
        `${user.userName} — ${user.nombre} ${user.apellido}`,
      ]),
    )

    const selection = await Swal.fire({
      title: 'Permisos efectivos',
      input: 'select',
      inputOptions: options,
      inputPlaceholder: 'Selecciona un usuario',
      showCancelButton: true,
      confirmButtonText: 'Consultar',
      cancelButtonText: 'Cancelar',
      inputValidator: (value) =>
        value
          ? undefined
          : 'Selecciona un usuario.',
    })

    if (!selection.isConfirmed) return

    try {
      const data =
        await apiRequest<EffectivePermission>(
          `/admin/folders/${folderId}/permissions/effective/${selection.value}`,
        )

      await Swal.fire({
        icon: 'info',
        title: 'Permisos efectivos',
        html: `
          <div class="effective-permissions">
            ${permissionBadges(data.value)}
          </div>
        `,
        confirmButtonText: 'Cerrar',
      })
    } catch (error) {
      await showError(error)
    }
  }

  const selectedFolder = useMemo(
    () =>
      folders.find(
        (folder) => folder.id === folderId,
      ),
    [folders, folderId],
  )

  return (
    <div className="admin-page">
      <div className="admin-title">
        <div>
          <h1>Permisos</h1>
          <p>
            Control de acceso a carpetas
          </p>
        </div>
      </div>

      <div className="permissions-folder-selector">
        <label htmlFor="permission-folder">
          Carpeta
        </label>

        <select
          id="permission-folder"
          value={folderId}
          disabled={loading}
          onChange={(event) =>
            void selectFolder(event.target.value)
          }
        >
          <option value="">
            Selecciona una carpeta
          </option>

          {folders.map((folder) => (
            <option
              key={folder.id}
              value={folder.id}
            >
              {folder.nombre}
            </option>
          ))}
        </select>
      </div>

      {!folderId ? (
        <div className="permissions-empty">
          Selecciona una carpeta para administrar
          sus permisos.
        </div>
      ) : (
        <>
          <div className="permissions-heading">
            <div>
              <strong>
                {selectedFolder?.nombre}
              </strong>
              <span>
                Permisos asignados directamente
              </span>
            </div>

            <div className="permissions-actions">
              <button
                type="button"
                onClick={() =>
                  void setUserPermission()
                }
              >
                + Usuario
              </button>

              <button
                type="button"
                onClick={() =>
                  void setGroupPermission()
                }
              >
                + Grupo
              </button>

              <button
                type="button"
                onClick={() =>
                  void viewEffectivePermission()
                }
              >
                Consultar efectivos
              </button>
            </div>
          </div>

          <div className="permissions-section">
            <h3>Usuarios</h3>

            {permissions.users.length === 0 ? (
              <div className="permissions-empty-small">
                No hay permisos directos para
                usuarios.
              </div>
            ) : (
              <div className="permissions-list">
                {permissions.users.map(
                  (permission) => {
                    const user = users.find(
                      (item) =>
                        item.id ===
                        permission.usuarioId,
                    )

                    return (
                      <div
                        className="permission-card"
                        key={permission.id}
                      >
                        <div className="permission-subject">
                          <strong>
                            {user?.userName ??
                              'Usuario'}
                          </strong>

                          <span>
                            {user
                              ? `${user.nombre} ${user.apellido}`
                              : permission.usuarioId}
                          </span>
                        </div>

                        <div className="permission-badges">
                          {renderPermissionBadges(
                            permission.permisos,
                          )}
                        </div>

                        <div className="permission-inherit">
                          {permission.heredarSubcarpetas
                            ? 'Incluye subcarpetas'
                            : 'Solo esta carpeta'}
                        </div>

                        <div className="row-actions">
                          <button
                            type="button"
                            onClick={() =>
                              void editUserPermission(
                                permission,
                              )
                            }
                          >
                            Editar
                          </button>

                          <button
                            type="button"
                            onClick={() =>
                              void deleteUserPermission(
                                permission,
                              )
                            }
                          >
                            Eliminar
                          </button>
                        </div>
                      </div>
                    )
                  },
                )}
              </div>
            )}
          </div>

          <div className="permissions-section">
            <h3>Grupos</h3>

            {permissions.groups.length === 0 ? (
              <div className="permissions-empty-small">
                No hay permisos directos para
                grupos.
              </div>
            ) : (
              <div className="permissions-list">
                {permissions.groups.map(
                  (permission) => {
                    const group = groups.find(
                      (item) =>
                        item.id ===
                        permission.grupoId,
                    )

                    return (
                      <div
                        className="permission-card"
                        key={permission.id}
                      >
                        <div className="permission-subject">
                          <strong>
                            {group?.nombre ??
                              'Grupo'}
                          </strong>

                          <span>Grupo</span>
                        </div>

                        <div className="permission-badges">
                          {renderPermissionBadges(
                            permission.permisos,
                          )}
                        </div>

                        <div className="permission-inherit">
                          {permission.heredarSubcarpetas
                            ? 'Incluye subcarpetas'
                            : 'Solo esta carpeta'}
                        </div>

                        <div className="row-actions">
                          <button
                            type="button"
                            onClick={() =>
                              void editGroupPermission(
                                permission,
                              )
                            }
                          >
                            Editar
                          </button>

                          <button
                            type="button"
                            onClick={() =>
                              void deleteGroupPermission(
                                permission,
                              )
                            }
                          >
                            Eliminar
                          </button>
                        </div>
                      </div>
                    )
                  },
                )}
              </div>
            )}
          </div>
        </>
      )}
    </div>
  )
}

interface PermissionDialogResult {
  subjectId: string
  permissions: number
  inherit: boolean
}

async function permissionDialog(
  title: string,
  subjects: Array<{
    id: string
    label: string
  }>,
  subjectLabel: string,
  selectedSubjectId = '',
  currentPermissions = 0,
  currentInherit = true,
  lockSubject = false,
): Promise<PermissionDialogResult | null> {
  const subjectOptions = subjects
    .map(
      (subject) => `
        <option
          value="${subject.id}"
          ${
            subject.id === selectedSubjectId
              ? 'selected'
              : ''
          }
        >
          ${escapeHtml(subject.label)}
        </option>
      `,
    )
    .join('')

  const checks = PERMISSIONS.map(
    (permission) => `
      <label class="permission-check">
        <input
          type="checkbox"
          class="permission-bit"
          value="${permission.value}"
          ${
            (currentPermissions &
              permission.value) !==
            0
              ? 'checked'
              : ''
          }
        >
        <span>${permission.label}</span>
      </label>
    `,
  ).join('')

  const result = await Swal.fire({
    title,
    html: `
      <div class="permission-dialog">
        <label>${subjectLabel}</label>

        <select
          id="permission-subject"
          class="swal2-select"
          ${lockSubject ? 'disabled' : ''}
        >
          <option value="">
            Selecciona
          </option>
          ${subjectOptions}
        </select>

        <label>Permisos</label>

        <div class="permission-checks">
          ${checks}
        </div>

        <label class="permission-inherit-check">
          <input
            id="permission-inherit"
            type="checkbox"
            ${currentInherit ? 'checked' : ''}
          >
          Aplicar también a subcarpetas
        </label>
      </div>
    `,
    width: 620,
    showCancelButton: true,
    confirmButtonText: 'Guardar',
    cancelButtonText: 'Cancelar',
    focusConfirm: false,
    preConfirm: () => {
      const subject = document.getElementById(
        'permission-subject',
      ) as HTMLSelectElement | null

      const subjectId =
        subject?.value || selectedSubjectId

      if (!subjectId) {
        Swal.showValidationMessage(
          `Selecciona un ${subjectLabel.toLowerCase()}.`,
        )
        return false
      }

      const selectedPermissions = Array.from(
        document.querySelectorAll<HTMLInputElement>(
          '.permission-bit:checked',
        ),
      ).reduce(
        (value, input) =>
          value | Number(input.value),
        0,
      )

      if (selectedPermissions === 0) {
        Swal.showValidationMessage(
          'Selecciona al menos un permiso.',
        )
        return false
      }

      const inherit =
        (
          document.getElementById(
            'permission-inherit',
          ) as HTMLInputElement | null
        )?.checked ?? false

      return {
        subjectId,
        permissions: selectedPermissions,
        inherit,
      }
    },
  })

  if (!result.isConfirmed || !result.value)
    return null

  return result.value
}

function renderPermissionBadges(value: number) {
  if (value === 31) {
    return (
      <span className="permission-badge permission-total">
        Control total
      </span>
    )
  }

  if (value === 0) {
    return (
      <span className="permission-badge">
        Ninguno
      </span>
    )
  }

  return PERMISSIONS.filter(
    (permission) =>
      (value & permission.value) !== 0,
  ).map((permission) => (
    <span
      className="permission-badge"
      key={permission.value}
    >
      {permission.label}
    </span>
  ))
}

function permissionBadges(value: number) {
  if (value === 31) {
    return `
      <span class="permission-badge permission-total">
        Control total
      </span>
    `
  }

  const permissions = PERMISSIONS.filter(
    (permission) =>
      (value & permission.value) !== 0,
  )

  if (permissions.length === 0) {
    return `
      <span class="permission-badge">
        Ninguno
      </span>
    `
  }

  return permissions
    .map(
      (permission) => `
        <span class="permission-badge">
          ${permission.label}
        </span>
      `,
    )
    .join('')
}

function escapeHtml(value: string) {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#039;')
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
    text:
      error instanceof Error
        ? error.message
        : 'Ocurrió un error inesperado.',
  })
}