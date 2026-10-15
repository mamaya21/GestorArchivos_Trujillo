import { useCallback, useEffect, useState } from 'react'
import { apiRequest } from '../api/client'

interface TrashFolder {
  type: 'folder'
  id: string
  nombre: string
  carpetaPadreId: string | null
  fechaEliminacion: string
}

interface TrashFile {
  type: 'file'
  id: string
  nombre: string
  carpetaId: string
  tamanoBytes: number
  fechaEliminacion: string
}

interface TrashContents {
  folders: TrashFolder[]
  files: TrashFile[]
}

export default function TrashPage() {
  const [data, setData] = useState<TrashContents>({
    folders: [],
    files: [],
  })

  const [search, setSearch] = useState('')
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState('')

  const load = useCallback(async () => {
    setLoading(true)
    setError('')

    try {
      const result = await apiRequest<TrashContents>('/trash')
      setData(result)
    } catch (err) {
      setError(getError(err))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const folders = data.folders.filter((x) =>
    x.nombre.toLowerCase().includes(search.toLowerCase()),
  )

  const files = data.files.filter((x) =>
    x.nombre.toLowerCase().includes(search.toLowerCase()),
  )

  async function restoreFolder(item: TrashFolder) {
    try {
      await apiRequest(`/trash/folders/${item.id}/restore`, {
        method: 'POST',
      })

      await load()
    } catch (err) {
      alert(getError(err))
    }
  }

  async function restoreFile(item: TrashFile) {
    try {
      await apiRequest(`/trash/files/${item.id}/restore`, {
        method: 'POST',
      })

      await load()
    } catch (err) {
      alert(getError(err))
    }
  }

  async function deleteFolder(item: TrashFolder) {
    if (
      !confirm(
        `¿Eliminar definitivamente la carpeta "${item.nombre}" y su contenido? Esta acción no se puede deshacer.`,
      )
    ) {
      return
    }

    try {
      await apiRequest(`/trash/folders/${item.id}`, {
        method: 'DELETE',
      })

      await load()
    } catch (err) {
      alert(getError(err))
    }
  }

  async function deleteFile(item: TrashFile) {
    if (
      !confirm(
        `¿Eliminar definitivamente "${item.nombre}"? Esta acción no se puede deshacer.`,
      )
    ) {
      return
    }

    try {
      await apiRequest(`/trash/files/${item.id}`, {
        method: 'DELETE',
      })

      await load()
    } catch (err) {
      alert(getError(err))
    }
  }

  return (
    <div className="file-manager">
      <div className="toolbar">
        <div>
          <h1>Papelera</h1>
          <div className="breadcrumbs">
            Elementos eliminados
          </div>
        </div>

        <input
          className="search-box"
          type="search"
          placeholder="Buscar en papelera..."
          value={search}
          onChange={(e) => setSearch(e.target.value)}
        />
      </div>

      {error && (
        <div className="error-message">
          {error}
        </div>
      )}

      <div className="explorer">
        <div className="trash-header">
          <span>Nombre</span>
          <span>Tamaño</span>
          <span>Eliminado</span>
          <span>Acciones</span>
        </div>

        {loading ? (
          <div className="empty-state">Cargando...</div>
        ) : (
          <>
            {folders.map((item) => (
              <div className="trash-row" key={`folder-${item.id}`}>
                <div className="item-name">
                  <span className="item-icon">📁</span>
                  {item.nombre}
                </div>

                <span>—</span>

                <span>{formatDate(item.fechaEliminacion)}</span>

                <div className="row-actions">
                  <button
                    type="button"
                    onClick={() => restoreFolder(item)}
                  >
                    Restaurar
                  </button>

                  <button
                    type="button"
                    onClick={() => deleteFolder(item)}
                  >
                    Eliminar definitivamente
                  </button>
                </div>
              </div>
            ))}

            {files.map((item) => (
              <div className="trash-row" key={`file-${item.id}`}>
                <div className="item-name">
                  <span className="item-icon">📄</span>
                  {item.nombre}
                </div>

                <span>{formatBytes(item.tamanoBytes)}</span>

                <span>{formatDate(item.fechaEliminacion)}</span>

                <div className="row-actions">
                  <button
                    type="button"
                    onClick={() => restoreFile(item)}
                  >
                    Restaurar
                  </button>

                  <button
                    type="button"
                    onClick={() => deleteFile(item)}
                  >
                    Eliminar definitivamente
                  </button>
                </div>
              </div>
            ))}

            {!folders.length && !files.length && (
              <div className="empty-state">
                La papelera está vacía.
              </div>
            )}
          </>
        )}
      </div>
    </div>
  )
}

function formatBytes(bytes: number) {
  if (!bytes) return '0 B'

  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const index = Math.floor(Math.log(bytes) / Math.log(1024))

  return `${(bytes / 1024 ** index).toFixed(
    index === 0 ? 0 : 1,
  )} ${units[index]}`
}

function formatDate(value: string) {
  return new Intl.DateTimeFormat('es-PE', {
    dateStyle: 'short',
    timeStyle: 'short',
  }).format(new Date(value))
}

function getError(error: unknown) {
  return error instanceof Error
    ? error.message
    : 'Ocurrió un error inesperado.'
}