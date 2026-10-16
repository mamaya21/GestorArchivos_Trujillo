import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type DragEvent,
} from 'react'
import { apiRaw, apiRequest } from '../api/client'
import Swal from 'sweetalert2'
import { activateSyncOnThisComputer } from '../sync/syncActivation'
import type {
  FileItem,
  Folder,
  FolderContents,
  UploadInitializeResponse,
  UploadStatusResponse,
} from '../types/api'

interface Breadcrumb {
  id: string | null
  nombre: string
}

interface UploadState {
  uploadId: string
  name: string
  size: number
  folderId: string
  chunkSize: number
  expectedChunkCount: number
  progress: number
  status: 'uploading' | 'paused'
}

interface StoredUpload {
  uploadId: string
  fileName: string
  fileSize: number
  folderId: string
  chunkSize: number
  expectedChunkCount: number
  lastModified: number
  replaceFileId?: string
  expectedVersion?: number
}

type SortField = 'name' | 'size' | 'date'
type SortDirection = 'asc' | 'desc'

const MAX_FILE_SIZE = 4 * 1024 * 1024 * 1024
const STORAGE_KEY = 'gestorarchivos.pendingUpload'

export default function FilesPage() {
  const [currentFolderId, setCurrentFolderId] =
    useState<string | null>(null)

  const [navigationHistory, setNavigationHistory] = useState<
    Breadcrumb[][]
  >([[{ id: null, nombre: 'Inicio' }]])

  const [historyIndex, setHistoryIndex] = useState(0)

  const [folders, setFolders] = useState<Folder[]>([])
  const [files, setFiles] = useState<FileItem[]>([])

  const [breadcrumbs, setBreadcrumbs] = useState<Breadcrumb[]>([
    { id: null, nombre: 'Inicio' },
  ])

  const [search, setSearch] = useState('')
  const [sortField, setSortField] = useState<SortField>('name')
  const [sortDirection, setSortDirection] =
    useState<SortDirection>('asc')

  const [selectedFolders, setSelectedFolders] =
    useState<Set<string>>(new Set())

  const [selectedFiles, setSelectedFiles] =
    useState<Set<string>>(new Set())

  const [loading, setLoading] = useState(false)
  const [dragging, setDragging] = useState(false)
  const [upload, setUpload] = useState<UploadState | null>(null)
  const [pendingUpload, setPendingUpload] =
    useState<StoredUpload | null>(null)

  const [message, setMessage] = useState('')
  const [error, setError] = useState('')

  const fileInput = useRef<HTMLInputElement>(null)
  const resumeInput = useRef<HTMLInputElement>(null)
  const pauseRequested = useRef(false)
  const cancelRequested = useRef(false)

  const loadContents = useCallback(async (folderId: string | null) => {
    setLoading(true)
    setError('')

    try {
      const query = folderId
        ? `?parentId=${encodeURIComponent(folderId)}`
        : ''

      const data =
        await apiRequest<FolderContents>(`/folders${query}`)

      setFolders(data.folders)
      setFiles(data.files)
      setSelectedFolders(new Set())
      setSelectedFiles(new Set())
    } catch (err) {
      setError(getError(err))
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void loadContents(currentFolderId)
  }, [currentFolderId, loadContents])

  useEffect(() => {
    const raw = localStorage.getItem(STORAGE_KEY)

    if (!raw) return

    try {
      const stored = JSON.parse(raw) as StoredUpload

      void apiRequest<UploadStatusResponse>(
        `/uploads/${stored.uploadId}/status`,
      )
        .then((status) => {
          const progress = Math.round(
            (status.receivedChunks.length /
              status.expectedChunkCount) *
              100,
          )

          setPendingUpload(stored)

          setUpload({
            uploadId: stored.uploadId,
            name: stored.fileName,
            size: stored.fileSize,
            folderId: stored.folderId,
            chunkSize: status.chunkSize,
            expectedChunkCount: status.expectedChunkCount,
            progress,
            status: 'paused',
          })
        })
        .catch(() => {
          localStorage.removeItem(STORAGE_KEY)
          setPendingUpload(null)
        })
    } catch {
      localStorage.removeItem(STORAGE_KEY)
    }
  }, [])

  const visibleFolders = useMemo(() => {
    const query = search.trim().toLocaleLowerCase('es')

    return folders
      .filter((folder) =>
        folder.nombre.toLocaleLowerCase('es').includes(query),
      )
      .sort((a, b) => {
        let result = 0

        if (sortField === 'name') {
          result = a.nombre.localeCompare(b.nombre, 'es', {
            sensitivity: 'base',
          })
        }

        if (sortField === 'date') {
          result =
            new Date(
              a.fechaModificacion ?? a.fechaCreacion,
            ).getTime() -
            new Date(
              b.fechaModificacion ?? b.fechaCreacion,
            ).getTime()
        }

        return sortDirection === 'asc' ? result : -result
      })
  }, [folders, search, sortField, sortDirection])

  const visibleFiles = useMemo(() => {
    const query = search.trim().toLocaleLowerCase('es')

    return files
      .filter((file) =>
        file.nombre.toLocaleLowerCase('es').includes(query),
      )
      .sort((a, b) => {
        let result = 0

        if (sortField === 'name') {
          result = a.nombre.localeCompare(b.nombre, 'es', {
            sensitivity: 'base',
          })
        }

        if (sortField === 'size') {
          result = a.tamanoBytes - b.tamanoBytes
        }

        if (sortField === 'date') {
          result =
            new Date(
              a.fechaModificacion ?? a.fechaCreacion,
            ).getTime() -
            new Date(
              b.fechaModificacion ?? b.fechaCreacion,
            ).getTime()
        }

        return sortDirection === 'asc' ? result : -result
      })
  }, [files, search, sortField, sortDirection])

  const selectedCount =
    selectedFolders.size + selectedFiles.size

  const allVisibleSelected =
    visibleFolders.length + visibleFiles.length > 0 &&
    visibleFolders.every((x) => selectedFolders.has(x.id)) &&
    visibleFiles.every((x) => selectedFiles.has(x.id))

  function navigateTo(path: Breadcrumb[]) {
    const target = path[path.length - 1]

    setSearch('')
    setBreadcrumbs(path)
    setCurrentFolderId(target.id)

    setNavigationHistory((current) => {
      const next = current.slice(0, historyIndex + 1)
      next.push(path)
      return next
    })

    setHistoryIndex((current) => current + 1)
  }

  function openFolder(folder: Folder) {
    navigateTo([
      ...breadcrumbs,
      {
        id: folder.id,
        nombre: folder.nombre,
      },
    ])
  }

  function navigateBreadcrumb(index: number) {
    navigateTo(breadcrumbs.slice(0, index + 1))
  }

  function navigateBack() {
    if (historyIndex <= 0) return

    const nextIndex = historyIndex - 1
    const path = navigationHistory[nextIndex]
    const target = path[path.length - 1]

    setSearch('')
    setBreadcrumbs(path)
    setCurrentFolderId(target.id)
    setHistoryIndex(nextIndex)
  }

  function navigateForward() {
    if (historyIndex >= navigationHistory.length - 1) return

    const nextIndex = historyIndex + 1
    const path = navigationHistory[nextIndex]
    const target = path[path.length - 1]

    setSearch('')
    setBreadcrumbs(path)
    setCurrentFolderId(target.id)
    setHistoryIndex(nextIndex)
  }

  function changeSort(field: SortField) {
    if (sortField === field) {
      setSortDirection((current) =>
        current === 'asc' ? 'desc' : 'asc',
      )
      return
    }

    setSortField(field)
    setSortDirection('asc')
  }

  function toggleFolder(id: string) {
    setSelectedFolders((current) => {
      const next = new Set(current)

      if (next.has(id)) next.delete(id)
      else next.add(id)

      return next
    })
  }

  function toggleFile(id: string) {
    setSelectedFiles((current) => {
      const next = new Set(current)

      if (next.has(id)) next.delete(id)
      else next.add(id)

      return next
    })
  }

  function toggleAll() {
    if (allVisibleSelected) {
      setSelectedFolders(new Set())
      setSelectedFiles(new Set())
      return
    }

    setSelectedFolders(
      new Set(visibleFolders.map((x) => x.id)),
    )

    setSelectedFiles(
      new Set(visibleFiles.map((x) => x.id)),
    )
  }

  async function createFolder() {
    const result = await Swal.fire({
      title: 'Nueva carpeta',
      input: 'text',
      inputLabel: 'Nombre de la carpeta',
      inputPlaceholder: 'Nombre',
      showCancelButton: true,
      confirmButtonText: 'Crear',
      cancelButtonText: 'Cancelar',
      inputValidator: (value) =>
        value.trim() ? undefined : 'Ingresa un nombre.',
    })

    if (!result.isConfirmed) return

    try {
      await apiRequest('/folders', {
        method: 'POST',
        body: JSON.stringify({
          name: result.value.trim(),
          parentId: currentFolderId,
        }),
      })

      await loadContents(currentFolderId)

      await Swal.fire({
        icon: 'success',
        title: 'Carpeta creada',
        timer: 1200,
        showConfirmButton: false,
      })
    } catch (error) {
      await showFileError(error)
    }
  }

  async function renameFolder(folder: Folder) {
    const result = await Swal.fire({
      title: 'Renombrar carpeta',
      input: 'text',
      inputValue: folder.nombre,
      showCancelButton: true,
      confirmButtonText: 'Guardar',
      cancelButtonText: 'Cancelar',
      inputValidator: (value) =>
        value.trim() ? undefined : 'Ingresa un nombre.',
    })

    if (
      !result.isConfirmed ||
      result.value.trim() === folder.nombre
    ) {
      return
    }

    try {
      await apiRequest(`/folders/${folder.id}/rename`, {
        method: 'PUT',
        body: JSON.stringify({
          name: result.value.trim(),
        }),
      })

      await loadContents(currentFolderId)
    } catch (error) {
      await showFileError(error)
    }
  }

  async function deleteFolder(folder: Folder) {
    const result = await Swal.fire({
      icon: 'warning',
      title: 'Mover a papelera',
      text: `¿Deseas mover "${folder.nombre}" a la papelera?`,
      showCancelButton: true,
      confirmButtonText: 'Mover',
      cancelButtonText: 'Cancelar',
    })

    if (!result.isConfirmed) return

    try {
      await apiRequest(`/folders/${folder.id}`, {
        method: 'DELETE',
      })

      await loadContents(currentFolderId)
    } catch (error) {
      await showFileError(error)
    }
}

  async function renameFile(file: FileItem) {
    const result = await Swal.fire({
      title: 'Renombrar archivo',
      input: 'text',
      inputValue: file.nombre,
      showCancelButton: true,
      confirmButtonText: 'Guardar',
      cancelButtonText: 'Cancelar',
      inputValidator: (value) =>
        value.trim() ? undefined : 'Ingresa un nombre.',
    })

    if (
      !result.isConfirmed ||
      result.value.trim() === file.nombre
    ) {
      return
    }

    try {
      await apiRequest(`/files/${file.id}/rename`, {
        method: 'PUT',
        body: JSON.stringify({
          name: result.value.trim(),
        }),
      })

      await loadContents(currentFolderId)
    } catch (error) {
      await showFileError(error)
    }
  }

  async function deleteFile(file: FileItem) {
    const result = await Swal.fire({
      icon: 'warning',
      title: 'Mover a papelera',
      text: `¿Deseas mover "${file.nombre}" a la papelera?`,
      showCancelButton: true,
      confirmButtonText: 'Mover',
      cancelButtonText: 'Cancelar',
    })

    if (!result.isConfirmed) return

    try {
      await apiRequest(`/files/${file.id}`, {
        method: 'DELETE',
      })

      await loadContents(currentFolderId)
    } catch (error) {
      await showFileError(error)
    }
  }

  async function deleteSelected() {
    if (!selectedCount) return

    const confirmation = await Swal.fire({
      icon: 'warning',
      title: 'Mover elementos a papelera',
      text: `¿Deseas mover ${selectedCount} elemento(s) a la papelera?`,
      showCancelButton: true,
      confirmButtonText: 'Mover',
      cancelButtonText: 'Cancelar',
    })

    if (!confirmation.isConfirmed) return

    setLoading(true)

    try {
      for (const id of selectedFiles) {
        await apiRequest(`/files/${id}`, {
          method: 'DELETE',
        })
      }

      for (const id of selectedFolders) {
        await apiRequest(`/folders/${id}`, {
          method: 'DELETE',
        })
      }

      setMessage(
        `${selectedCount} elemento(s) movido(s) a la papelera.`,
      )

      await loadContents(currentFolderId)
    } catch (err) {
      setError(getError(err))
      await loadContents(currentFolderId)
    } finally {
      setLoading(false)
    }
  }

  function openFile(file: FileItem) {
    window.open(
      `/api/files/${file.id}/open`,
      '_blank',
      'noopener,noreferrer',
    )
  }

  function downloadFile(file: FileItem) {
    const link = document.createElement('a')

    link.href = `/api/files/${file.id}/download`
    link.download = file.nombre

    document.body.appendChild(link)
    link.click()
    link.remove()
  }

  async function editTextFile(file: FileItem) {
    if (upload) {
      await Swal.fire({
        icon: 'warning',
        title: 'Carga en curso',
        text: 'Termina o cancela la carga pendiente antes de editar.',
      })
      return
    }

    const editableExtensions = [
      '.txt', '.md', '.json', '.xml', '.csv',
      '.html', '.css', '.js', '.ts', '.log',
    ]

    if (!editableExtensions.includes(file.extension.toLowerCase())) {
      await Swal.fire({
        icon: 'info',
        title: 'Edición no disponible',
        text: 'Este tipo de archivo requiere un editor compatible.',
      })
      return
    }

    const maxEditableSize = 2 * 1024 * 1024

    if (file.tamanoBytes > maxEditableSize) {
      await Swal.fire({
        icon: 'warning',
        title: 'Archivo demasiado grande',
        text: 'El editor web admite archivos de texto de hasta 2 MB.',
      })
      return
    }

    try {
      const response = await apiRaw(`/files/${file.id}/open`)

      if (!response.ok) {
        throw new Error('No se pudo obtener el contenido del archivo.')
      }

      const originalContent = await response.text()

      const result = await Swal.fire({
        title: `Editar: ${file.nombre}`,
        input: 'textarea',
        inputValue: originalContent,
        inputAttributes: {
          'aria-label': 'Contenido del archivo',
          spellcheck: 'false',
        },
        width: 850,
        showCancelButton: true,
        confirmButtonText: 'Guardar cambios',
        cancelButtonText: 'Cancelar',
        inputValidator: (value) => {
          if (value === originalContent) {
            return 'No hay cambios para guardar.'
          }

          if (!value.trim()) {
            return 'El contenido no puede estar vacío.'
          }

          return undefined
        },
      })

      if (!result.isConfirmed) return

      const updatedFile = new File(
        [result.value],
        file.nombre,
        {
          type: 'text/plain;charset=utf-8',
          lastModified: Date.now(),
        },
      )

      await startUpload(updatedFile)
    } catch (error) {
      await showFileError(error)
    }
}

  async function startUpload(file: File) {
    if (!currentFolderId) {
      alert(
        'Debes ingresar a una carpeta antes de cargar archivos.',
      )
      return
    }

    if (file.size <= 0 || file.size > MAX_FILE_SIZE) {
      alert(`"${file.name}" tiene un tamaño inválido.`)
      return
    }

    if (upload) {
      alert(
        'Ya existe una carga pendiente. Termínala o cancélala primero.',
      )
      return
    }

    const existing = files.find(x =>
      x.nombre.normalize('NFC').toLocaleLowerCase('es') ===
      file.name.normalize('NFC').toLocaleLowerCase('es'))

    let replacement: { id: string; version: number } | undefined
    if (existing) {
      const choice = await Swal.fire({
        icon: 'warning',
        title: 'Ya existe un archivo con ese nombre',
        text: `"${existing.nombre}"\n\n¿Quieres reemplazarlo con el archivo que estás subiendo? El archivo anterior será sustituido por el nuevo.`,
        showCancelButton: true,
        confirmButtonText: 'Reemplazar archivo',
        cancelButtonText: 'Cancelar',
        reverseButtons: true,
        width: 480,
      })
      if (!choice.isConfirmed) return
      const current = await apiRequest<{ id: string; version: number; nombre: string }>(
        `/files/${existing.id}`,
      ).catch(async (error: unknown) => {
        await showFileError(error)
        return null
      })
      if (!current) return
      if (!Number.isSafeInteger(current.version) || current.version < 1) {
        await Swal.fire({ icon: 'error', title: 'Versión inválida', text: 'No se pudo verificar la versión actual del archivo.' })
        return
      }
      replacement = { id: current.id, version: current.version }
    }

    try {
      setError('')
      setMessage('')
      pauseRequested.current = false
      cancelRequested.current = false

      const initialization =
        await apiRequest<UploadInitializeResponse>(
          '/uploads/initialize',
          {
            method: 'POST',
            body: JSON.stringify({
              folderId: currentFolderId,
              fileName: file.name,
              fileSize: file.size,
              replaceFileId: replacement?.id,
              expectedVersion: replacement?.version,
              contentType:
                file.type || 'application/octet-stream',
            }),
          },
        )

      const stored: StoredUpload = {
        uploadId: initialization.uploadId,
        fileName: file.name,
        fileSize: file.size,
        folderId: currentFolderId,
        chunkSize: initialization.chunkSize,
        expectedChunkCount:
          initialization.expectedChunkCount,
        lastModified: file.lastModified,
        replaceFileId: replacement?.id,
        expectedVersion: replacement?.version,
      }

      localStorage.setItem(
        STORAGE_KEY,
        JSON.stringify(stored),
      )

      setPendingUpload(stored)

      setUpload({
        uploadId: initialization.uploadId,
        name: file.name,
        size: file.size,
        folderId: currentFolderId,
        chunkSize: initialization.chunkSize,
        expectedChunkCount:
          initialization.expectedChunkCount,
        progress: 0,
        status: 'uploading',
      })

      await continueUpload(file, stored)
    } catch (err) {
      setError(`${file.name}: ${getError(err)}`)
    }
  }

  async function continueUpload(
    file: File,
    stored: StoredUpload,
  ) {
    try {
      pauseRequested.current = false
      cancelRequested.current = false

      const status =
        await apiRequest<UploadStatusResponse>(
          `/uploads/${stored.uploadId}/status`,
        )

      if (
        file.name !== stored.fileName ||
        file.size !== stored.fileSize ||
        file.lastModified !== stored.lastModified
      ) {
        throw new Error(
          'El archivo seleccionado no coincide con la carga pendiente.',
        )
      }

      const received = new Set(status.receivedChunks)

      setUpload({
        uploadId: stored.uploadId,
        name: stored.fileName,
        size: stored.fileSize,
        folderId: stored.folderId,
        chunkSize: status.chunkSize,
        expectedChunkCount: status.expectedChunkCount,
        progress: Math.round(
          (received.size / status.expectedChunkCount) * 100,
        ),
        status: 'uploading',
      })

      for (
        let index = 0;
        index < status.expectedChunkCount;
        index++
      ) {
        if (cancelRequested.current) return

        if (pauseRequested.current) {
          setUpload((current) =>
            current
              ? {
                  ...current,
                  status: 'paused',
                }
              : current,
          )

          return
        }

        if (received.has(index)) continue

        const start = index * status.chunkSize

        const end = Math.min(
          start + status.chunkSize,
          file.size,
        )

        await apiRaw(
          `/uploads/${stored.uploadId}/chunks/${index}`,
          {
            method: 'PUT',
            headers: {
              'Content-Type': 'application/octet-stream',
            },
            body: file.slice(start, end),
          },
        )

        received.add(index)

        setUpload((current) =>
          current
            ? {
                ...current,
                progress: Math.round(
                  (received.size /
                    status.expectedChunkCount) *
                    100,
                ),
              }
            : current,
        )
      }

      if (
        pauseRequested.current ||
        cancelRequested.current
      ) {
        return
      }

      await apiRequest(
        `/uploads/${stored.uploadId}/complete`,
        {
          method: 'POST',
        },
      )

      localStorage.removeItem(STORAGE_KEY)
      setPendingUpload(null)
      setUpload(null)

      setMessage(
        `"${file.name}" cargado correctamente.`,
      )

      await loadContents(stored.folderId)
    } catch (err) {
      setUpload((current) =>
        current
          ? {
              ...current,
              status: 'paused',
            }
          : current,
      )

      setError(getError(err))
    }
  }

  function pauseUpload() {
    pauseRequested.current = true
  }

  async function cancelUpload() {
    const current = upload

    if (!current) return

    const confirmation = await Swal.fire({
      icon: 'warning',
      title: 'Cancelar carga',
      text: `¿Deseas cancelar la carga de "${current.name}"?`,
      showCancelButton: true,
      confirmButtonText: 'Cancelar carga',
      cancelButtonText: 'Continuar cargando',
    })

    if (!confirmation.isConfirmed) return

    cancelRequested.current = true
    pauseRequested.current = true

    try {
      await apiRequest(`/uploads/${current.uploadId}`, {
        method: 'DELETE',
      })
    } catch (err) {
      setError(getError(err))
      return
    }

    localStorage.removeItem(STORAGE_KEY)
    setPendingUpload(null)
    setUpload(null)
    setMessage('Carga cancelada.')
  }

  function requestResume() {
    if (!pendingUpload) return

    resumeInput.current?.click()
  }

  async function handleResumeFile(file: File) {
    if (!pendingUpload) return

    await continueUpload(file, pendingUpload)
  }

  async function uploadFiles(selected: File[]) {
    if (selected.length === 0) return

    if (upload) {
      alert(
        'Termina o cancela la carga pendiente antes de iniciar otra.',
      )
      return
    }

    for (const file of selected) {
      await startUpload(file)

      if (localStorage.getItem(STORAGE_KEY)) {
        break
      }
    }
  }

  function handleDrop(event: DragEvent<HTMLDivElement>) {
    event.preventDefault()
    setDragging(false)

    const dropped = Array.from(event.dataTransfer.files)

    if (dropped.length) {
      void uploadFiles(dropped)
    }
  }

  async function openSyncManagement() {
    try {
      const devices = await apiRequest<Array<{
        deviceId: string
        deviceName: string
        rootFolderName: string
        active: boolean
        lastConnectionUtc: string | null
      }>>('/sync/devices')

      const active = devices.filter((device) => device.active)
      const rows = active.map((device) =>
        `<div style="padding:8px 0;text-align:left;border-bottom:1px solid #ddd"><strong>${escapeSyncHtml(device.deviceName)}</strong><br/><small>Carpeta: ${escapeSyncHtml(device.rootFolderName)} · ${device.lastConnectionUtc ? 'Última conexión registrada' : 'Sin conexión registrada'}</small></div>`,
      ).join('')

      const result = await Swal.fire({
        title: 'Sincronización',
        html: active.length
          ? `<p>Equipos vinculados: ${active.length}</p>${rows}<p style="font-size:13px">Para modificar carpetas, abre el selector de Windows. Pausar, reanudar y desvincular estarán disponibles cuando el control local esté implementado.</p>`
          : '<p>No hay equipos activos vinculados a esta cuenta.</p>',
        showCancelButton: true,
        showConfirmButton: active.length === 0,
        confirmButtonText: 'Activar sincronización',
        cancelButtonText: 'Cerrar',
        width: 540,
      })

      if (!result.isConfirmed) return

      await activateSyncOnThisComputer()
      await Swal.fire({
        icon: 'success',
        title: 'Dispositivo registrado',
        text: 'El equipo quedó registrado. Configura las carpetas desde el selector de Windows.',
      })
    } catch (error) {
      await Swal.fire({
        icon: 'error',
        title: 'No se pudo administrar la sincronización',
        text: getError(error),
      })
    }
  }

  return (
    <div
      className="file-manager"
      onDragEnter={(event) => {
        event.preventDefault()
        setDragging(true)
      }}
      onDragOver={(event) => {
        event.preventDefault()
        setDragging(true)
      }}
      onDragLeave={(event) => {
        if (event.currentTarget === event.target) {
          setDragging(false)
        }
      }}
      onDrop={handleDrop}
    >
      <div className="toolbar">
        <div>

          <div className="folder-navigation">
            <button
              type="button"
              title="Atrás"
              disabled={historyIndex <= 0}
              onClick={navigateBack}
            >
              ←
            </button>

            <button
              type="button"
              title="Adelante"
              disabled={historyIndex >= navigationHistory.length - 1}
              onClick={navigateForward}
            >
              →
            </button>
          </div>

          <h1>Archivos</h1>

          <div className="breadcrumbs">
            {breadcrumbs.map((item, index) => (
              <span key={item.id ?? 'root'}>
                {index > 0 && <b>/</b>}

                <button
                  type="button"
                  onClick={() =>
                    navigateBreadcrumb(index)
                  }
                >
                  {item.nombre}
                </button>
              </span>
            ))}
          </div>
        </div>

        <div className="toolbar-actions">
          <button
            type="button"
            onClick={() => void openSyncManagement()}
          >
            Activar sincronización
          </button>

          <button type="button" onClick={createFolder}>
            + Nueva carpeta
          </button>

          <button
            type="button"
            disabled={!currentFolderId || !!upload}
            onClick={() => fileInput.current?.click()}
          >
            ↑ Subir archivos
          </button>

          <input
            ref={fileInput}
            type="file"
            multiple
            hidden
            onChange={(event) => {
              const selected = Array.from(
                event.target.files ?? [],
              )

              event.target.value = ''

              if (selected.length) {
                void uploadFiles(selected)
              }
            }}
          />

          <input
            ref={resumeInput}
            type="file"
            hidden
            onChange={(event) => {
              const file = event.target.files?.[0]
              event.target.value = ''

              if (file) {
                void handleResumeFile(file)
              }
            }}
          />
        </div>
      </div>

      <div className="explorer-tools">
        <input
          className="search-box"
          type="search"
          placeholder="Buscar archivos y carpetas..."
          value={search}
          onChange={(event) =>
            setSearch(event.target.value)
          }
        />

        {selectedCount > 0 && (
          <div className="selection-actions">
            <strong>
              {selectedCount} seleccionado(s)
            </strong>

            <button
              type="button"
              onClick={() => void deleteSelected()}
            >
              Mover a papelera
            </button>
          </div>
        )}
      </div>

      {currentFolderId && (
        <div className="drop-help">
          Arrastra archivos aquí para subirlos
        </div>
      )}

      {upload && (
        <div className="upload-box">
          <div className="upload-info">
            <span>
              {upload.status === 'paused'
                ? 'Carga pausada: '
                : 'Subiendo: '}
              {upload.name}
            </span>

            <strong>{upload.progress}%</strong>
          </div>

          <progress
            value={upload.progress}
            max="100"
          />

          <div className="upload-actions">
            {upload.status === 'uploading' ? (
              <button
                type="button"
                onClick={pauseUpload}
              >
                Pausar
              </button>
            ) : (
              <button
                type="button"
                onClick={requestResume}
              >
                Reanudar
              </button>
            )}

            <button
              type="button"
              onClick={() => void cancelUpload()}
            >
              Cancelar
            </button>
          </div>
        </div>
      )}

      {message && (
        <div className="success-message">
          {message}
        </div>
      )}

      {error && (
        <div className="error-message">{error}</div>
      )}

      {dragging && currentFolderId && !upload && (
        <div className="drop-overlay">
          Suelta los archivos para cargarlos
        </div>
      )}

      <div className="explorer">
        <div className="explorer-header explorer-header-select">
          <span>
            <input
              type="checkbox"
              checked={allVisibleSelected}
              onChange={toggleAll}
            />
          </span>

          <button
            className="sort-button"
            type="button"
            onClick={() => changeSort('name')}
          >
            Nombre{' '}
            {sortIndicator(
              sortField,
              'name',
              sortDirection,
            )}
          </button>

          <button
            className="sort-button"
            type="button"
            onClick={() => changeSort('size')}
          >
            Tamaño{' '}
            {sortIndicator(
              sortField,
              'size',
              sortDirection,
            )}
          </button>

          <button
            className="sort-button"
            type="button"
            onClick={() => changeSort('date')}
          >
            Modificado{' '}
            {sortIndicator(
              sortField,
              'date',
              sortDirection,
            )}
          </button>

          <span>Acciones</span>
        </div>

        {loading ? (
          <div className="empty-state">
            Cargando...
          </div>
        ) : (
          <>
            {visibleFolders.map((folder) => (
              <div
                className="explorer-row explorer-row-select"
                key={folder.id}
                onDoubleClick={() =>
                  openFolder(folder)
                }
              >
                <span>
                  <input
                    type="checkbox"
                    checked={selectedFolders.has(
                      folder.id,
                    )}
                    onChange={() =>
                      toggleFolder(folder.id)
                    }
                    onDoubleClick={(event) =>
                      event.stopPropagation()
                    }
                  />
                </span>

                <button
                  className="item-name"
                  type="button"
                  onClick={() => openFolder(folder)}
                >
                  <span className="item-icon">
                    📁
                  </span>
                  {folder.nombre}
                </button>

                <span>—</span>

                <span>
                  {formatDate(
                    folder.fechaModificacion ??
                      folder.fechaCreacion,
                  )}
                </span>

                <div className="row-actions">

                  <button
                    type="button"
                    onClick={() =>
                      renameFolder(folder)
                    }
                  >
                    Renombrar
                  </button>

                  <button
                    type="button"
                    onClick={() =>
                      deleteFolder(folder)
                    }
                  >
                    Eliminar
                  </button>
                </div>
              </div>
            ))}

            {visibleFiles.map((file) => (
              <div
                className="explorer-row explorer-row-select"
                key={file.id}
                onDoubleClick={() =>
                  openFile(file)
                }
              >
                <span>
                  <input
                    type="checkbox"
                    checked={selectedFiles.has(file.id)}
                    onChange={() =>
                      toggleFile(file.id)
                    }
                    onDoubleClick={(event) =>
                      event.stopPropagation()
                    }
                  />
                </span>

                <button
                  className="item-name"
                  type="button"
                  onClick={(event) => {
                    event.stopPropagation()
                    toggleFile(file.id)
                  }}
                >
                  <span className="item-icon">
                    {fileIcon(file)}
                  </span>

                  {file.nombre}
                </button>

                <span>
                  {formatBytes(file.tamanoBytes)}
                </span>

                <span>
                  {formatDate(
                    file.fechaModificacion ??
                      file.fechaCreacion,
                  )}
                </span>

                <div className="row-actions">
                  <button
                    type="button"
                    onClick={() => openFile(file)}
                  >
                    Abrir
                  </button>

                  <button
                    type="button"
                    onClick={() =>
                      downloadFile(file)
                    }
                  >
                    Descargar
                  </button>

                  {[
                    '.txt', '.md', '.json', '.xml', '.csv',
                    '.html', '.css', '.js', '.ts', '.log',
                  ].includes(file.extension.toLowerCase()) && (
                    <button
                      type="button"
                      disabled={!!upload}
                      onClick={() => void editTextFile(file)}
                    >
                      Editar
                    </button>
                  )}

                  <button
                    type="button"
                    onClick={() =>
                      renameFile(file)
                    }
                  >
                    Renombrar
                  </button>

                  <button
                    type="button"
                    onClick={() =>
                      deleteFile(file)
                    }
                  >
                    Eliminar
                  </button>
                </div>
              </div>
            ))}

            {!visibleFolders.length &&
              !visibleFiles.length && (
                <div className="empty-state">
                  {search
                    ? 'No se encontraron resultados.'
                    : 'Esta carpeta está vacía.'}
                </div>
              )}
          </>
        )}
      </div>
    </div>
  )
}

function sortIndicator(
  current: SortField,
  field: SortField,
  direction: SortDirection,
) {
  if (current !== field) return ''

  return direction === 'asc' ? '↑' : '↓'
}

function formatBytes(bytes: number) {
  if (!bytes) return '0 B'

  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  const index = Math.floor(
    Math.log(bytes) / Math.log(1024),
  )

  return `${(
    bytes /
    1024 ** index
  ).toFixed(index === 0 ? 0 : 1)} ${units[index]}`
}

function formatDate(value: string) {
  const normalized = /(?:Z|[+-]\d{2}:\d{2})$/i.test(value)
    ? value
    : `${value}Z`

  const date = new Date(normalized)

  if (Number.isNaN(date.getTime())) {
    return '—'
  }

  return new Intl.DateTimeFormat('es-PE', {
    timeZone: 'America/Lima',
    dateStyle: 'short',
    timeStyle: 'short',
  }).format(date)
}

function fileIcon(file: FileItem) {
  const extension = file.extension.toLowerCase()

  if (extension === '.pdf') return '📕'

  if (
    [
      '.jpg',
      '.jpeg',
      '.png',
      '.gif',
      '.webp',
    ].includes(extension)
  ) {
    return '🖼️'
  }

  if (['.doc', '.docx'].includes(extension))
    return '📘'

  if (
    ['.xls', '.xlsx', '.csv'].includes(extension)
  )
    return '📗'

  if (['.ppt', '.pptx'].includes(extension))
    return '📙'

  if (['.zip', '.rar', '.7z'].includes(extension))
    return '🗜️'

  return '📄'
}

async function showFileError(error: unknown) {
  await Swal.fire({
    icon: 'error',
    title: 'Error',
    text: getError(error),
  })
}

function getError(error: unknown) {
  return error instanceof Error
    ? error.message
    : 'Ocurrió un error inesperado.'
}

function escapeSyncHtml(value: string): string {
  return value.replace(/[&<>"']/g, (character) => {
    const entities: Record<string, string> = {
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }
    return entities[character] ?? character
  })
}
