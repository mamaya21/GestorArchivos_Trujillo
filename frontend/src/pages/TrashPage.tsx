import { useCallback, useEffect, useState } from 'react'
import { ApiError, apiRequest } from '../api/client'
import Swal from 'sweetalert2'

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

type TrashItem = TrashFolder | TrashFile

function itemKey(item: TrashItem) {
  return `${item.type}:${item.id}`
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
  const [selected, setSelected] = useState<string[]>([])
  const [processing, setProcessing] = useState(false)
  const [notice, setNotice] = useState('')

  const load = useCallback(async () => {
    setLoading(true)
    setError('')

    try {
      const result = await apiRequest<TrashContents>('/trash')
      setData(result)
      const available = new Set([...result.folders, ...result.files].map(itemKey))
      setSelected(previous => previous.filter(key => available.has(key)))
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

  const visibleItems: TrashItem[] = [...folders, ...files]
  const selectedItems: TrashItem[] = [...data.folders, ...data.files]
    .filter(item => selected.includes(itemKey(item)))
  const allVisibleSelected = visibleItems.length > 0 &&
    visibleItems.every(item => selected.includes(itemKey(item)))

  function toggle(item: TrashItem) {
    const key = itemKey(item)
    setSelected(previous => previous.includes(key)
      ? previous.filter(value => value !== key)
      : [...previous, key])
  }

  function toggleAll() {
    const keys = visibleItems.map(itemKey)
    setSelected(previous => keys.every(key => previous.includes(key))
      ? previous.filter(key => !keys.includes(key))
      : Array.from(new Set([...previous, ...keys])))
  }

  async function execute(items: TrashItem[], action: 'restore' | 'delete') {
    if (processing || !items.length) return
    if (action === 'delete' && !confirm(
      `¿Eliminar definitivamente ${items.length} elemento(s) y el contenido de sus carpetas? Esta acción no se puede deshacer.`
    )) return

    setProcessing(true)
    setError('')
    setNotice('')
    const failed: string[] = []
    const failedKeys: string[] = []
    let succeeded = 0
    let skipped = 0
    let applyToAll: 'skip' | 'keepBoth' | 'replace' | null = null

    // Reintentar carpetas pendientes cuando la restauración de su padre
    // permita completar una operación posterior.
    let pending = [...items].sort((a, b) =>
      a.type === b.type ? 0 : a.type === 'folder' ? -1 : 1)
    const attempts = action === 'restore' ? pending.length : 1

    try {
      for (let pass = 0; pass < attempts && pending.length; pass++) {
        const retry: typeof pending = []
        let progress = false
        for (const item of pending) {
          const url = `/trash/${item.type === 'folder' ? 'folders' : 'files'}/${item.id}`
          try {
            await apiRequest(action === 'restore' ? `${url}/restore` : url, {
              method: action === 'restore' ? 'POST' : 'DELETE',
            })
            succeeded++
            progress = true
          } catch (err) {
            if (action === 'restore' && err instanceof ApiError && err.status === 409 &&
                /mismo nombre|ese nombre/i.test(err.message)) {
              let resolution: 'skip' | 'keepBoth' | 'replace' | null = applyToAll === 'replace' && item.type === 'folder' ? null : applyToAll
              if (!resolution) {
                const choice = await Swal.fire({
                  icon: 'warning',
                  title: 'Conflicto al restaurar',
                  text: `Ya existe un elemento llamado "${item.nombre}".`,
                  showCancelButton: true,
                  confirmButtonText: 'Continuar',
                  cancelButtonText: 'Cancelar operación',
                  confirmButtonColor: '#2563eb',
                  width: 480,
                  customClass: { popup: 'ga-conflict-dialog' },
                  html: `<div class="ga-conflict-options" role="radiogroup" aria-label="Resolver conflicto">
                    <label class="ga-conflict-option"><input type="radio" name="ga-resolution" value="keepBoth" checked /><span><strong>Conservar ambos</strong><small>Restaurar con un nombre diferente.</small></span></label>
                    ${item.type === 'file' ? '<label class="ga-conflict-option"><input type="radio" name="ga-resolution" value="replace" /><span><strong>Reemplazar archivo</strong><small>Conservar la versión anterior en el historial.</small></span></label>' : ''}
                    <label class="ga-conflict-option"><input type="radio" name="ga-resolution" value="skip" /><span><strong>Omitir</strong><small>No restaurar este elemento y continuar.</small></span></label>
                  </div><label class="ga-conflict-apply"><input type="checkbox" id="trash-all" /> Aplicar a todos los conflictos</label>`,
                  preConfirm: () => ({
                    resolution: Swal.getHtmlContainer()?.querySelector<HTMLInputElement>('input[name="ga-resolution"]:checked')?.value ?? 'keepBoth',
                    all: Boolean(Swal.getHtmlContainer()?.querySelector<HTMLInputElement>('#trash-all')?.checked),
                  }),
                })
                if (choice.isDismissed) {
                  setNotice(`${succeeded} restaurado(s) antes de cancelar.`)
                  return
                }
                const selected = choice.value as { resolution: 'skip' | 'keepBoth' | 'replace'; all: boolean } | undefined
                resolution = selected?.resolution ?? 'keepBoth'
                if (selected?.all) applyToAll = resolution
              }
              try {
                const response = await apiRequest<{ skipped?: boolean } | undefined>(
                  `${url}/restore?conflictResolution=${resolution}`,
                  { method: 'POST' },
                )
                if (response?.skipped) skipped++
                else succeeded++
                progress = true
                continue
              } catch (retryError) {
                failedKeys.push(itemKey(item))
                failed.push(`${item.nombre}: ${getError(retryError)}`)
                continue
              }
            }
            if (action === 'restore' && pass < attempts - 1) {
              retry.push(item)
            } else {
              failedKeys.push(itemKey(item))
              failed.push(`${item.nombre}: ${getError(err)}`)
            }
          }
        }
        pending = retry
        if (!progress) {
          // No seguir reintentando errores permanentes.
          for (const item of pending) {
            failedKeys.push(itemKey(item))
            failed.push(`${item.nombre}: no se pudo completar la restauración.`)
          }
          break
        }
      }

      setSelected(failedKeys)
      setNotice(`${succeeded} procesados correctamente, ${skipped} omitidos de ${items.length} elementos.`)
      if (failed.length) setError(failed.join('\n'))
      try {
        const result = await apiRequest<TrashContents>('/trash')
        setData(result)
        const available = new Set([...result.folders, ...result.files].map(itemKey))
        setSelected(previous => previous.filter(key => available.has(key)))
      } catch (err) {
        setError(previous => [previous, getError(err)].filter(Boolean).join('\n'))
      }
    } finally {
      setProcessing(false)
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
        <div className="error-message" style={{ whiteSpace: 'pre-wrap' }}>
          {error}
        </div>
      )}

      <div className="trash-selection-toolbar">
        <label className="trash-select-all">
          <input type="checkbox" checked={allVisibleSelected}
            disabled={loading || processing || !visibleItems.length}
            onChange={toggleAll} />
          {' '}Seleccionar todos los visibles
        </label>
        <span className="trash-selection-count">{selectedItems.length} seleccionados</span>
        <div className="trash-bulk-actions">
          <button className="trash-btn trash-btn-primary" type="button" disabled={processing || !selectedItems.length}
            onClick={() => void execute(selectedItems, 'restore')}>↩ Restaurar</button>
          <button className="trash-btn trash-btn-danger" type="button" disabled={processing || !selectedItems.length}
            onClick={() => void execute(selectedItems, 'delete')}>🗑 Eliminar definitivamente</button>
          <button className="trash-btn trash-btn-secondary" type="button" disabled={processing || !selectedItems.length}
            onClick={() => setSelected([])}>Deseleccionar</button>
        </div>
      </div>
      {notice && <div role="status">{notice}</div>}
      {processing && <div>Procesando elementos...</div>}
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
                  <input type="checkbox" checked={selected.includes(itemKey(item))}
                    disabled={processing} aria-label={`Seleccionar ${item.nombre}`}
                    onChange={() => toggle(item)} />
                  <span className="item-icon">📁</span>
                  {item.nombre}
                </div>

                <span>—</span>

                <span>{formatDate(item.fechaEliminacion)}</span>

                <div className="row-actions">
                  <button
                    type="button"
                    disabled={processing}
                    onClick={() => void execute([item], 'restore')}
                  >
                    Restaurar
                  </button>

                  <button
                    type="button"
                    disabled={processing}
                    onClick={() => void execute([item], 'delete')}
                  >
                    Eliminar definitivamente
                  </button>
                </div>
              </div>
            ))}

            {files.map((item) => (
              <div className="trash-row" key={`file-${item.id}`}>
                <div className="item-name">
                  <input type="checkbox" checked={selected.includes(itemKey(item))}
                    disabled={processing} aria-label={`Seleccionar ${item.nombre}`}
                    onChange={() => toggle(item)} />
                  <span className="item-icon">📄</span>
                  {item.nombre}
                </div>

                <span>{formatBytes(item.tamanoBytes)}</span>

                <span>{formatDate(item.fechaEliminacion)}</span>

                <div className="row-actions">
                  <button
                    type="button"
                    disabled={processing}
                    onClick={() => void execute([item], 'restore')}
                  >
                    Restaurar
                  </button>

                  <button
                    type="button"
                    disabled={processing}
                    onClick={() => void execute([item], 'delete')}
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