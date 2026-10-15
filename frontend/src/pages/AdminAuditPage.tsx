import {
  useCallback,
  useEffect,
  useMemo,
  useState,
} from 'react'
import Swal from 'sweetalert2'
import { apiRequest } from '../api/client'

interface AuditItem {
  id: number
  usuarioId: string | null
  accion: string
  entidad: string
  entidadId: string
  datosAnteriores: string | null
  datosNuevos: string | null
  fechaUtc: string
}

interface AuditResponse {
  page: number
  pageSize: number
  total: number
  items: AuditItem[]
}

interface AdminUser {
  id: string
  userName: string
  nombre: string
  apellido: string
}

const PAGE_SIZE = 50

export default function AdminAuditPage() {
  const [items, setItems] = useState<AuditItem[]>([])
  const [users, setUsers] = useState<AdminUser[]>([])
  const [page, setPage] = useState(1)
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(true)
  const [search, setSearch] = useState('')
  const [expandedAuditId, setExpandedAuditId] =
    useState<number | null>(null)

  const loadAudit = useCallback(
    async (targetPage: number) => {
      setLoading(true)

      try {
        const data =
          await apiRequest<AuditResponse>(
            `/admin/audit?page=${targetPage}&pageSize=${PAGE_SIZE}`,
          )

        setItems(data.items)
        setTotal(data.total)
        setPage(data.page)
        setExpandedAuditId(null)
      } catch (error) {
        await showError(error)
      } finally {
        setLoading(false)
      }
    },
    [],
  )

  const loadUsers = useCallback(async () => {
    try {
      const data =
        await apiRequest<AdminUser[]>(
          '/admin/users',
        )

      setUsers(data)
    } catch {
      // La auditoría puede mostrarse usando el ID.
    }
  }, [])

  useEffect(() => {
    void Promise.all([
      loadAudit(1),
      loadUsers(),
    ])
  }, [loadAudit, loadUsers])

  const totalPages = Math.max(
    1,
    Math.ceil(total / PAGE_SIZE),
  )

  const visibleItems = useMemo(() => {
    const query = search
      .trim()
      .toLocaleLowerCase('es')

    if (!query) return items

    return items.filter((item) => {
      const user = findUser(
        users,
        item.usuarioId,
      )

      return [
        item.accion,
        item.entidad,
        item.entidadId,
        user?.userName ?? '',
        user?.nombre ?? '',
        user?.apellido ?? '',
        item.usuarioId ?? '',
      ]
        .join(' ')
        .toLocaleLowerCase('es')
        .includes(query)
    })
  }, [items, users, search])

  async function showDetails(
    item: AuditItem,
  ) {
    const user = findUser(
      users,
      item.usuarioId,
    )

    await Swal.fire({
      title: 'Detalle de auditoría',
      width: 760,
      confirmButtonText: 'Cerrar',
      html: `
        <div class="audit-detail">
          <div>
            <strong>Fecha</strong>
            <span>
              ${escapeHtml(
                formatDate(item.fechaUtc),
              )}
            </span>
          </div>

          <div>
            <strong>Usuario</strong>
            <span>
              ${escapeHtml(
                user
                  ? `${user.userName} — ${user.nombre} ${user.apellido}`
                  : item.usuarioId ??
                      'Sistema / no identificado',
              )}
            </span>
          </div>

          <div>
            <strong>Acción</strong>
            <span>
              ${escapeHtml(
                formatAction(item.accion),
              )}
            </span>
          </div>

          <div>
            <strong>Entidad</strong>
            <span>
              ${escapeHtml(item.entidad)}
            </span>
          </div>

          <div>
            <strong>ID entidad</strong>
            <span>
              ${escapeHtml(
                item.entidadId || '—',
              )}
            </span>
          </div>

          <div class="audit-detail-full">
            <strong>
              Datos anteriores
            </strong>
            ${formatAuditData(
              item.datosAnteriores,
            )}
          </div>

          <div class="audit-detail-full">
            <strong>
              Datos nuevos
            </strong>
            ${formatAuditData(
              item.datosNuevos,
            )}
          </div>
        </div>
      `,
    })
  }

  return (
    <div className="admin-page">
      <div className="admin-title">
        <div>
          <h1>Auditoría</h1>

          <p>
            Historial de operaciones registradas
            en el sistema
          </p>
        </div>
      </div>

      <div className="explorer-tools audit-tools">
        <input
          className="search-box"
          type="search"
          placeholder="Buscar en esta página..."
          value={search}
          onChange={(event) =>
            setSearch(event.target.value)
          }
        />

        <strong>
          {total} registro(s)
        </strong>
      </div>

      <div className="admin-table-wrapper">
        <table className="admin-table admin-audit-table">
          <thead>
            <tr>
              <th>Fecha</th>
              <th>Usuario</th>
              <th>Acción</th>
              <th>Entidad</th>
              <th>ID entidad</th>
              <th>Detalle</th>
            </tr>
          </thead>

          <tbody>
            {loading ? (
              <tr>
                <td colSpan={6}>
                  Cargando...
                </td>
              </tr>
            ) : (
              visibleItems.map((item) => {
                const user = findUser(
                  users,
                  item.usuarioId,
                )

                const expanded =
                  expandedAuditId === item.id

                return (
                  <tr
                    key={item.id}
                    className={
                      expanded
                        ? 'admin-user-expanded'
                        : ''
                    }
                    onClick={() =>
                      setExpandedAuditId(
                        (current) =>
                          current === item.id
                            ? null
                            : item.id,
                      )
                    }
                  >
                    <td>
                      {formatDate(
                        item.fechaUtc,
                      )}
                    </td>

                    <td>
                      {user?.userName ??
                        shortId(
                          item.usuarioId,
                        )}
                    </td>

                    <td>
                      <span className="audit-action">
                        {formatAction(
                          item.accion,
                        )}
                      </span>
                    </td>

                    <td>
                      {item.entidad}
                    </td>

                    <td
                      title={
                        item.entidadId
                      }
                    >
                      {shortId(
                        item.entidadId,
                      )}
                    </td>

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
                            void showDetails(
                              item,
                            )
                          }
                        >
                          Ver detalle
                        </button>
                      </div>
                    </td>
                  </tr>
                )
              })
            )}

            {!loading &&
              visibleItems.length === 0 && (
                <tr>
                  <td colSpan={6}>
                    No se encontraron registros.
                  </td>
                </tr>
              )}
          </tbody>
        </table>
      </div>

      <div className="audit-pagination">
        <button
          type="button"
          disabled={
            loading || page <= 1
          }
          onClick={() =>
            void loadAudit(page - 1)
          }
        >
          ← Anterior
        </button>

        <span>
          Página{' '}
          <strong>{page}</strong> de{' '}
          <strong>{totalPages}</strong>
        </span>

        <button
          type="button"
          disabled={
            loading ||
            page >= totalPages
          }
          onClick={() =>
            void loadAudit(page + 1)
          }
        >
          Siguiente →
        </button>
      </div>
    </div>
  )
}

function findUser(
  users: AdminUser[],
  userId: string | null,
) {
  if (!userId) return undefined

  return users.find(
    (user) => user.id === userId,
  )
}

function formatAction(
  action: string,
) {
  const names: Record<
    string,
    string
  > = {
    CREATE: 'Creación',
    UPDATE: 'Modificación',
    DELETE: 'Eliminación',
    LOGIN_SUCCESS: 'Inicio de sesión',
    LOGIN_FAILED: 'Login fallido',
    LOGIN_LOCKED: 'Usuario bloqueado',
    LOGOUT: 'Cierre de sesión',
    PASSWORD_CHANGED:
      'Cambio de contraseña',
    PASSWORD_CHANGE_FAILED:
      'Cambio de contraseña fallido',
  }

  return names[action] ?? action
}

function formatAuditData(
  value: string | null,
) {
  if (!value) {
    return `
      <div class="audit-json audit-json-empty">
        Sin datos
      </div>
    `
  }

  try {
    const parsed = JSON.parse(value)

    return `
      <pre class="audit-json">${escapeHtml(
        JSON.stringify(
          parsed,
          null,
          2,
        ),
      )}</pre>
    `
  } catch {
    return `
      <pre class="audit-json">${escapeHtml(
        value,
      )}</pre>
    `
  }
}

function shortId(
  value: string | null,
) {
  if (!value) return '—'

  if (value.length <= 18) {
    return value
  }

  return `${value.slice(
    0,
    8,
  )}…${value.slice(-6)}`
}

function formatDate(
  value: string,
) {
  return new Intl.DateTimeFormat(
    'es-PE',
    {
      dateStyle: 'short',
      timeStyle: 'medium',
    },
  ).format(new Date(value))
}

function escapeHtml(
  value: string,
) {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#039;')
}

async function showError(
  error: unknown,
) {
  await Swal.fire({
    icon: 'error',
    title: 'Error',
    text:
      error instanceof Error
        ? error.message
        : 'Ocurrió un error inesperado.',
  })
}