import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type DragEvent,
  type MouseEvent,
  type PointerEvent,
} from 'react'
import { ApiError, apiRaw, apiRequest } from '../api/client'
import Swal from 'sweetalert2'
import { activateSyncOnThisComputer } from '../sync/syncActivation'
import type {
  FileItem,
  Folder,
  FolderContents,
  UploadInitializeResponse,
  UploadStatusResponse,
} from '../types/api'

interface ContextMenuState {
  x: number
  y: number
  kind: 'file' | 'folder' | 'background'
  file?: FileItem
  folder?: Folder
}

interface MoveClipboard {
  folderIds: string[]
  fileIds: string[]
  sourceFolderId: string | null
}

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

  const [copyClipboard, setCopyClipboard] = useState<MoveClipboard | null>(null)
  const longPressTimer = useRef<ReturnType<typeof setTimeout> | null>(null)
  const touchStart = useRef<{ x: number; y: number } | null>(null)
  const [contextMenu, setContextMenu] = useState<ContextMenuState | null>(null)
  const contextMenuRef = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!contextMenu) return
    const dismiss = (event: globalThis.PointerEvent) => {
      if (!contextMenuRef.current?.contains(event.target as Node)) setContextMenu(null)
    }
    const dismissContext = (event: globalThis.MouseEvent) => {
      if (!contextMenuRef.current?.contains(event.target as Node)) setContextMenu(null)
    }
    const dismissKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') setContextMenu(null)
    }
    const dismissScroll = () => setContextMenu(null)
    document.addEventListener('pointerdown', dismiss, true)
    document.addEventListener('contextmenu', dismissContext, true)
    document.addEventListener('keydown', dismissKey)
    window.addEventListener('scroll', dismissScroll, true)
    return () => {
      document.removeEventListener('pointerdown', dismiss, true)
      document.removeEventListener('contextmenu', dismissContext, true)
      document.removeEventListener('keydown', dismissKey)
      window.removeEventListener('scroll', dismissScroll, true)
    }
  }, [contextMenu])
  const [treeRefresh, setTreeRefresh] = useState(0)
  const [moveClipboard, setMoveClipboard] = useState<MoveClipboard | null>(null)
  const [moving, setMoving] = useState(false)
  const [draggedItem, setDraggedItem] = useState<{ kind: 'file' | 'folder'; id: string } | null>(null)
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
      setTreeRefresh((value) => value + 1)
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

  async function createTextFile() {
    if (!currentFolderId || upload) return

    const result = await Swal.fire({
      title: 'Nuevo archivo de texto',
      input: 'text',
      inputLabel: 'Nombre del archivo',
      inputValue: 'Nuevo documento.txt',
      showCancelButton: true,
      confirmButtonText: 'Crear',
      cancelButtonText: 'Cancelar',
      inputValidator: (value) => {
        const name = value.trim()
        if (!name) return 'Ingresa un nombre.'
        if (!name.toLowerCase().endsWith('.txt')) return 'El archivo debe tener extensión .txt.'
        if (name === '.' || name === '..' || /[<>:"/\\|?*]/.test(name) || [...name].some(char => char.charCodeAt(0) < 32) || name.endsWith('.') || name.endsWith(' ')) return 'Nombre de archivo no válido.'
        return undefined
      },
    })
    if (!result.isConfirmed || !result.value) return

    const name = result.value.trim()
    try {
      await apiRequest('/files/empty', {
        method: 'POST',
        body: JSON.stringify({ folderId: currentFolderId, name }),
      })
      await loadContents(currentFolderId)
      setMessage(`"${name}" creado correctamente.`)
    } catch (error) {
      await showFileError(error)
    }
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

  function cutSelected() {
    if (!selectedCount || moving || !currentFolderId) return
    setMoveClipboard({
      folderIds: [...selectedFolders],
      fileIds: [...selectedFiles],
      sourceFolderId: currentFolderId,
    })
    setSelectedFolders(new Set())
    setSelectedFiles(new Set())
    setCopyClipboard(null)
    setMessage('Elementos cortados. Abre la carpeta de destino y selecciona Pegar.')
  }

  async function findFolderRoot(folderId: string): Promise<string | null> {
    const visited = new Set<string>()
    let current: string | null = folderId
    while (current && !visited.has(current)) {
      visited.add(current)
      const folder: Folder = await apiRequest<Folder>(`/folders/${current}`)
      if (!folder.carpetaPadreId) return folder.id
      current = folder.carpetaPadreId
    }
    return null
  }

  async function moveItems(clipboard: MoveClipboard, destinationFolderId: string | null) {
    if (!destinationFolderId || moving || upload) return
    if (!clipboard.sourceFolderId) {
      await Swal.fire({ icon: 'warning', title: 'Raíz protegida', text: 'No puedes mover una carpeta raíz.' })
      return
    }
    try {
      const [sourceRoot, destinationRoot] = await Promise.all([
        findFolderRoot(clipboard.sourceFolderId),
        findFolderRoot(destinationFolderId),
      ])
      if (!sourceRoot || sourceRoot !== destinationRoot) {
        await Swal.fire({ icon: 'warning', title: 'Movimiento bloqueado', text: 'Solo puedes mover elementos dentro de la misma raíz.' })
        return
      }
    } catch (error) {
      await showFileError(error)
      return
    }
    if (clipboard.sourceFolderId === destinationFolderId) {
      setMessage('Los elementos ya están en esta carpeta. Elige otra carpeta para moverlos.')
      return
    }
    if (clipboard.folderIds.includes(destinationFolderId)) {
      await Swal.fire({ icon: 'warning', title: 'Destino inválido', text: 'No puedes mover una carpeta dentro de sí misma.' })
      return
    }
    if (clipboard.folderIds.length) {
      try {
        for (const id of clipboard.folderIds) {
          const seen = new Set<string>()
          let cursor: string | null = destinationFolderId
          while (cursor && !seen.has(cursor)) {
            if (cursor === id) {
              await Swal.fire({ icon: 'warning', title: 'Destino inválido', text: 'No puedes mover una carpeta dentro de una de sus subcarpetas.' })
              return
            }
            seen.add(cursor)
            const ancestor: Folder = await apiRequest<Folder>(`/folders/${cursor}`)
            cursor = ancestor.carpetaPadreId
          }
        }
      } catch (error) {
        await showFileError(error)
        return
      }
    }
    const confirmation = await Swal.fire({
      title: 'Mover elementos',
      text: `¿Mover ${clipboard.folderIds.length + clipboard.fileIds.length} elemento(s) a esta carpeta?`,
      icon: 'question',
      showCancelButton: true,
      confirmButtonText: 'Mover',
      cancelButtonText: 'Cancelar',
    })
    if (!confirmation.isConfirmed) return
    setMoving(true)
    let completed = 0
    let skipped = 0
    setMoving(true)
    try {
      // Consultar el destino real: al arrastrar sobre otra carpeta, la lista
      // visible todavía pertenece al origen y no permite detectar conflictos.
      const destinationContents = await apiRequest<FolderContents>(
        `/folders?parentId=${encodeURIComponent(destinationFolderId)}`,
      )
      const existingNames = new Set(
        [...destinationContents.folders, ...destinationContents.files]
          .map(item => item.nombre.normalize('NFC').toLocaleLowerCase('es')),
      )
      const pending = [
        ...clipboard.folderIds.map(id => ({ id, kind: 'folders' as const })),
        ...clipboard.fileIds.map(id => ({ id, kind: 'files' as const })),
      ]
      let applyToAll: 'skip' | 'keepBoth' | null = null

      for (const item of pending) {
        const source = await apiRequest<{ nombre: string }>(`/${item.kind}/${item.id}`)
        let resolution: 'skip' | 'keepBoth' | undefined
        const normalized = source.nombre.normalize('NFC').toLocaleLowerCase('es')
        if (existingNames.has(normalized)) {
          if (applyToAll) {
            resolution = applyToAll
          } else {
            const choice = await Swal.fire({
              icon: 'warning',
              title: 'Nombre duplicado',
              text: `Ya existe "${source.nombre}" en el destino. ¿Qué deseas hacer?`,
              showCancelButton: true,
              showDenyButton: true,
              confirmButtonText: 'Conservar ambos',
              denyButtonText: 'Omitir',
              cancelButtonText: 'Cancelar operación',
              input: 'checkbox',
              inputValue: '0',
              inputPlaceholder: 'Aplicar a todos los conflictos',
            })
            if (choice.isDismissed) break
            resolution = choice.isConfirmed ? 'keepBoth' : 'skip'
            if (choice.value === 1 || choice.value === '1') applyToAll = resolution
          }
        }
        const result = await apiRequest<{ skipped?: boolean } | undefined>(`/${item.kind}/${item.id}/move`, {
          method: 'PUT',
          body: JSON.stringify({ destinationFolderId, conflictResolution: resolution }),
        })
        if (result?.skipped) {
          skipped++
        } else {
          completed++
          existingNames.add(normalized)
        }
      }
      setMoveClipboard(null)
      setMessage(`${completed} movido(s), ${skipped} omitido(s).`)
    } catch (err) {
      setError(`${completed} movido(s). Operación interrumpida: ${getError(err)}`)
      setMoveClipboard(null)
    } finally {
      setMoving(false)
      await loadContents(currentFolderId)
    }
  }

  function handleInternalDragStart(event: DragEvent<HTMLDivElement>, kind: 'file' | 'folder', id: string) {
    if (!currentFolderId || moving || upload) { event.preventDefault(); return }
    const selected = kind === 'folder' ? selectedFolders.has(id) : selectedFiles.has(id)
    const folderIds = selected ? [...selectedFolders] : kind === 'folder' ? [id] : []
    const fileIds = selected ? [...selectedFiles] : kind === 'file' ? [id] : []
    const clipboard: MoveClipboard = { folderIds, fileIds, sourceFolderId: currentFolderId }
    event.dataTransfer.effectAllowed = 'copyMove'
    event.dataTransfer.setData('application/x-gestorarchivos-item', JSON.stringify(clipboard))
    setDraggedItem({ kind, id })
  }

  function handleInternalDrop(event: DragEvent<HTMLElement>, destinationFolderId: string | null) {
    const raw = event.dataTransfer.getData('application/x-gestorarchivos-item')
    if (!raw) return false
    event.preventDefault()
    event.stopPropagation()
    setDraggedItem(null)
    setDragging(false)
    try {
      const data: unknown = JSON.parse(raw)
      if (!data || typeof data !== 'object') throw new Error('Selección inválida')
      const value = data as Partial<MoveClipboard> & { kind?: string; id?: string }
      const folderIds = Array.isArray(value.folderIds) ? value.folderIds : value.kind === 'folder' && typeof value.id === 'string' ? [value.id] : []
      const fileIds = Array.isArray(value.fileIds) ? value.fileIds : value.kind === 'file' && typeof value.id === 'string' ? [value.id] : []
      if (![...folderIds, ...fileIds].every(id => typeof id === 'string') || !folderIds.length && !fileIds.length) throw new Error('Selección inválida')
      const clipboard: MoveClipboard = { folderIds, fileIds, sourceFolderId: value.sourceFolderId ?? currentFolderId }
      if (event.ctrlKey) {
        setMoveClipboard(null)
        setCopyClipboard(clipboard)
        void pasteCopiedSelection(clipboard, destinationFolderId)
      } else {
        void moveItems(clipboard, destinationFolderId)
      }
    } catch {
      setError('No se pudo interpretar la selección arrastrada.')
    }
    return true
  }

  async function pasteCopy() {
    if (!copyClipboard) return
    await pasteCopiedSelection(copyClipboard, currentFolderId)
  }

  async function pasteCopiedSelection(clipboard: MoveClipboard, destinationFolderId: string | null) {
    if (!destinationFolderId || upload || moving) return
    let completed = 0
    let skipped = 0
    let applyToAll: 'skip' | 'keepBoth' | 'replace' | null = null
    setMoving(true)
    setError('')
    try {
      const entries = [
        ...clipboard.folderIds.map(id => ({ id, kind: 'folders' as const })),
        ...clipboard.fileIds.map(id => ({ id, kind: 'files' as const })),
      ]
      for (const entry of entries) {
        const source = await apiRequest<{ nombre: string }>(`/${entry.kind}/${entry.id}`)
        const payload = {
          destinationFolderId,
          fileIds: entry.kind === 'files' ? [entry.id] : [],
          folderIds: entry.kind === 'folders' ? [entry.id] : [],
        }
        let resolution: 'skip' | 'keepBoth' | 'replace' | undefined = applyToAll ?? undefined
        let result: { skipped?: boolean } | undefined
        try {
          result = await apiRequest('/files/copy-selection', {
            method: 'POST',
            body: JSON.stringify({ ...payload, conflictResolution: resolution }),
          })
        } catch (err) {
          if (!(err instanceof ApiError) || err.status !== 409) throw err
          if (!resolution) {
            const choice = await Swal.fire({
              icon: 'warning',
              title: 'Nombre duplicado',
              text: `Ya existe "${source.nombre}" en la carpeta de destino. ¿Qué deseas hacer?`,
              showDenyButton: true,
              footer: 'Para reemplazar, selecciona la opción Reemplazar en el selector.',
              input: 'radio',
              inputOptions: { keepBoth: 'Conservar ambos', replace: 'Reemplazar (guardar versión anterior)', skip: 'Omitir' },
              inputValue: 'keepBoth',
              showCancelButton: true,
              confirmButtonText: 'Aplicar',
              denyButtonText: 'Omitir',
              cancelButtonText: 'Cancelar operación',
              html: '<label><input type="checkbox" id="copy-conflict-all" /> Aplicar a todos los conflictos</label>',
              preConfirm: (value) => ({
                resolution: value as 'skip' | 'keepBoth' | 'replace',
                applyToAll: Boolean(Swal.getHtmlContainer()?.querySelector<HTMLInputElement>('#copy-conflict-all')?.checked),
              }),
            })
            if (choice.isDismissed) break
            const selection = choice.value as { resolution: 'skip' | 'keepBoth' | 'replace'; applyToAll: boolean } | undefined
            resolution = choice.isDenied ? 'skip' : (selection?.resolution ?? 'keepBoth')
            if (selection?.applyToAll) applyToAll = resolution
          }
          result = await apiRequest('/files/copy-selection', {
            method: 'POST',
            body: JSON.stringify({ ...payload, conflictResolution: resolution }),
          })
        }
        if (result?.skipped) skipped++
        else completed++
      }
      setCopyClipboard(null)
      setMessage(`${completed} copiado(s), ${skipped} omitido(s).`)
    } catch (error) {
      setError(`${completed} copiado(s). Operación interrumpida: ${getError(error)}`)
    } finally {
      setMoving(false)
      await loadContents(currentFolderId)
    }
  }

  async function downloadSelection(fileIds: string[], folderIds: string[] = []) {
    if (fileIds.length === 1 && folderIds.length === 0) {
      const file = files.find(item => item.id === fileIds[0])
      if (file) downloadFile(file)
      return
    }
    try {
      const response = await apiRaw('/files/download-zip', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ fileIds, folderIds }),
      })
      if (!response.ok) throw new Error(await response.text())
      const url = URL.createObjectURL(await response.blob())
      const link = document.createElement('a')
      link.href = url
      link.download = 'archivos-seleccionados.zip'
      document.body.appendChild(link)
      link.click()
      link.remove()
      setTimeout(() => URL.revokeObjectURL(url), 60000)
    } catch (error) {
      await showFileError(error)
    }
  }

  async function deleteSelection(folderIds: string[], fileIds: string[]) {
    if (!folderIds.length && !fileIds.length || moving) return
    const confirmation = await Swal.fire({
      icon: 'warning',
      title: 'Mover selección a papelera',
      text: `¿Mover ${folderIds.length + fileIds.length} elemento(s) a la papelera?`,
      showCancelButton: true,
      confirmButtonText: 'Mover a papelera',
      cancelButtonText: 'Cancelar',
    })
    if (!confirmation.isConfirmed) return
    let completed = 0
    setMoving(true)
    try {
      for (const id of fileIds) {
        await apiRequest(`/files/${id}`, { method: 'DELETE' })
        completed++
      }
      for (const id of folderIds) {
        await apiRequest(`/folders/${id}`, { method: 'DELETE' })
        completed++
      }
      setMessage(`${completed} elemento(s) enviado(s) a la papelera.`)
    } catch (error) {
      setError(`${completed} enviado(s). Operación interrumpida: ${getError(error)}`)
    } finally {
      setMoving(false)
      await loadContents(currentFolderId)
    }
  }

  function cancelLongPress() {
    if (longPressTimer.current) clearTimeout(longPressTimer.current)
    longPressTimer.current = null
    touchStart.current = null
  }

  function touchMenu(kind: ContextMenuState['kind'], file?: FileItem, folder?: Folder) {
    return {
      onPointerDown: (event: PointerEvent<HTMLElement>) => {
        if (event.pointerType !== 'touch' || (event.target as HTMLElement).closest('button,input')) return
        cancelLongPress()
        const x = event.clientX, y = event.clientY
        touchStart.current = { x, y }
        longPressTimer.current = setTimeout(() => {
          setContextMenu({ x: Math.min(x, window.innerWidth - 220), y: Math.min(y, window.innerHeight - 360), kind, file, folder })
          longPressTimer.current = null
        }, 550)
      },
      onPointerMove: (event: PointerEvent<HTMLElement>) => {
        if (touchStart.current && Math.hypot(event.clientX - touchStart.current.x, event.clientY - touchStart.current.y) > 12) cancelLongPress()
      },
      onPointerUp: cancelLongPress,
      onPointerCancel: cancelLongPress,
      onPointerLeave: cancelLongPress,
    }
  }

  function showContextMenu(event: MouseEvent<HTMLElement>, kind: ContextMenuState['kind'], file?: FileItem, folder?: Folder) {
    event.preventDefault()
    event.stopPropagation()
    setContextMenu({ x: Math.min(event.clientX, window.innerWidth - 220), y: Math.min(event.clientY, window.innerHeight - 360), kind, file, folder })
  }

  function contextAction(action: string) {
    const menu = contextMenu
    setContextMenu(null)
    if (!menu) return
    const file = menu.file
    const folder = menu.folder
    const isSelected = Boolean((file && selectedFiles.has(file.id)) || (folder && selectedFolders.has(folder.id)))
    const folderIds = isSelected ? [...selectedFolders] : folder ? [folder.id] : []
    const fileIds = isSelected ? [...selectedFiles] : file ? [file.id] : []
    const multiple = folderIds.length + fileIds.length > 1
    if (folder && !folder.carpetaPadreId && (action === 'rename' || action === 'delete' || action === 'cut' || action === 'copy')) return
    if (multiple && ['open', 'edit', 'rename'].includes(action)) return
    if (action === 'paste') {
      if (moveClipboard) void moveItems(moveClipboard, currentFolderId)
      else if (copyClipboard) void pasteCopy()
      return
    }
    if (action === 'new-folder') { void createFolder(); return }
    if (action === 'new-text-file') { void createTextFile(); return }
    if (action === 'upload') { fileInput.current?.click(); return }
    if (action === 'open') {
      if (file) openFile(file)
      else if (folder) openFolder(folder)
    }
    if (action === 'download' && (fileIds.length || folderIds.length)) void downloadSelection(fileIds, folderIds)
    if (action === 'edit' && file) void editTextFile(file)
    if (action === 'versions' && file) void showFileVersions(file)
    if (action === 'rename') {
      if (file) void renameFile(file)
      else if (folder) void renameFolder(folder)
    }
    if (action === 'delete') {
      if (multiple) void deleteSelection(folderIds, fileIds)
      else if (file) void deleteFile(file)
      else if (folder) void deleteFolder(folder)
    }
    if (action === 'copy' && (fileIds.length || folderIds.length)) {
      setCopyClipboard({ folderIds, fileIds, sourceFolderId: currentFolderId })
      setMoveClipboard(null)
      setSelectedFolders(new Set())
      setSelectedFiles(new Set())
      setMessage(`${fileIds.length + folderIds.length} elemento(s) copiado(s) al portapapeles. Abre la carpeta de destino y selecciona Pegar.`)
    }
    if (action === 'cut') {
      if (!currentFolderId) return
      if (folderIds.length + fileIds.length === 0) return
      setCopyClipboard(null)
      setMoveClipboard({ folderIds, fileIds, sourceFolderId: currentFolderId })
      setSelectedFolders(new Set())
      setSelectedFiles(new Set())
      setMessage(`${folderIds.length + fileIds.length} elemento(s) cortado(s). Abre la carpeta de destino y selecciona Pegar.`)
    }
  }

  async function showFileVersions(file: FileItem) {
    try {
      const data = await apiRequest<{
        current: number
        history: Array<{ id: string; numero: number; tamanoBytes: number; fechaUtc: string }>
      }>(`/files/${file.id}/versions`)
      if (!data.history.length) {
        await Swal.fire({ icon: 'info', title: 'Historial de versiones', text: 'Este archivo todavía no tiene versiones anteriores.' })
        return
      }
      const choices: Record<string, string> = {}
      for (const version of data.history) {
        choices[version.id] = `Versión ${version.numero} — ${new Date(version.fechaUtc).toLocaleString()} — ${version.tamanoBytes} bytes`
      }
      const choice = await Swal.fire({
        title: `Versiones de ${file.nombre}`,
        input: 'select',
        inputOptions: choices,
        inputPlaceholder: 'Selecciona una versión',
        showCancelButton: true,
        showDenyButton: true,
        confirmButtonText: 'Restaurar versión',
        denyButtonText: 'Descargar versión',
        cancelButtonText: 'Cancelar',
        inputValidator: value => value ? undefined : 'Selecciona una versión.',
      })
      if (choice.isDismissed || !choice.value) return
      const versionId = String(choice.value)
      if (choice.isDenied) {
        const response = await apiRaw(`/files/${file.id}/versions/${versionId}/download`)
        if (!response.ok) throw new Error('No se pudo descargar la versión.')
        const blob = await response.blob()
        const url = URL.createObjectURL(blob)
        const link = document.createElement('a')
        link.href = url
        link.download = file.nombre
        link.click()
        URL.revokeObjectURL(url)
        return
      }
      if (choice.isConfirmed) {
        const confirmation = await Swal.fire({
          icon: 'question',
          title: 'Restaurar versión',
          text: 'La versión actual quedará guardada en el historial. ¿Continuar?',
          showCancelButton: true,
          confirmButtonText: 'Restaurar',
          cancelButtonText: 'Cancelar',
        })
        if (!confirmation.isConfirmed) return
        await apiRequest(`/files/${file.id}/versions/${versionId}/restore`, { method: 'POST' })
        setMessage(`Versión restaurada: ${file.nombre}.`)
        await loadContents(currentFolderId)
      }
    } catch (error) {
      await showFileError(error)
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
    if (handleInternalDrop(event, currentFolderId)) return
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
        `<div style="padding:8px 0;text-align:left;border-bottom:1px solid #ddd"><strong>${escapeSyncHtml(device.deviceName)}</strong><br/><small>Carpeta: ${escapeSyncHtml(device.rootFolderName)}</small></div>`,
      ).join('')

      // El estado de pausa es local a esta computadora, no global a la cuenta.
      let localState: { paused: boolean; deviceId?: string } | null = null
      try {
        const response = await fetch('http://127.0.0.1:47831/control')
        if (response.ok) localState = await response.json() as { paused: boolean; deviceId?: string }
      } catch {
        // El cliente local puede no estar instalado o ejecutándose.
      }

      const linkedHere = active.find(d => d.deviceId === localState?.deviceId)
      const result = await Swal.fire({
        title: 'Sincronización',
        html: `${active.length ? rows : '<p>No hay equipos activos.</p>'}
          <p style="font-size:13px">Estado de este equipo: ${localState === null ? 'Cliente Windows no disponible' : localState.paused ? 'Pausado' : 'En ejecución'}</p>
          <p style="font-size:13px">Las selecciones se administran desde el selector de Windows. Desvincular conserva los archivos locales y remotos.</p>`,
        showCancelButton: true,
        showDenyButton: linkedHere !== undefined,
        showConfirmButton: active.length === 0 || linkedHere !== undefined,
        confirmButtonText: active.length === 0 ? 'Activar' : localState?.paused ? 'Reanudar' : 'Pausar',
        denyButtonText: 'Desvincular este equipo',
        cancelButtonText: 'Cerrar',
        width: 540,
      })

      if (result.isDenied && linkedHere) {
        const confirm = await Swal.fire({
          icon: 'warning', title: 'Desvincular este equipo',
          text: 'Se revocará la conexión. Los archivos de Windows y del servidor se conservarán.',
          showCancelButton: true, confirmButtonText: 'Desvincular', cancelButtonText: 'Cancelar',
        })
        if (!confirm.isConfirmed) return
        // Pausar primero, revocar en el servidor y retirar las credenciales locales.
        // Ninguna de estas acciones elimina archivos ni carpetas.
        const pauseResponse = await fetch('http://127.0.0.1:47831/control', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ paused: true }),
        })
        if (!pauseResponse.ok) throw new Error('No se pudo pausar el cliente local')
        const stopResponse = await fetch('http://127.0.0.1:47831/quiesce', { method: 'POST' })
        if (!stopResponse.ok) throw new Error('No se pudo detener y confirmar el cierre de las transferencias activas')
        await apiRequest(`/sync/devices/${linkedHere.deviceId}/unlink`, { method: 'POST' })
        const deactivateResponse = await fetch('http://127.0.0.1:47831/deactivate', {
          method: 'POST',
        })
        if (!deactivateResponse.ok) throw new Error('Se revocó el dispositivo remoto, pero no se pudo limpiar el registro local')
        await Swal.fire({
          icon: 'success',
          title: 'Equipo desvinculado de forma segura',
          text: 'Los archivos locales y remotos se conservaron. Reinicia el servicio Windows antes de activar una sincronización nueva.',
        })
        return
      }
      if (!result.isConfirmed) return

      if (active.length === 0) {
        const previous = devices.filter(device => !device.active)
        let reuseDeviceId: string | undefined
        let reuseDeviceName: string | undefined
        if (previous.length > 0) {
          const choice = await Swal.fire({
            title: 'Configurar sincronización',
            input: 'select',
            inputOptions: {
              new: 'Registrar como equipo nuevo (crear carpeta nueva)',
              ...Object.fromEntries(previous.map(device => [device.deviceId,
                `Revincular ${device.deviceName} (conservar carpeta ${device.rootFolderName})`])),
            },
            inputValue: 'new',
            showCancelButton: true,
            confirmButtonText: 'Continuar',
            cancelButtonText: 'Cancelar',
          })
          if (!choice.isConfirmed) return
          if (choice.value !== 'new') {
            const selected = previous.find(device => device.deviceId === choice.value)
            if (!selected) throw new Error('Dispositivo anterior no válido')
            reuseDeviceId = selected.deviceId
            reuseDeviceName = selected.deviceName
          }
        }
        await activateSyncOnThisComputer(reuseDeviceId, reuseDeviceName)
        await Swal.fire({ icon: 'success', title: 'Dispositivo registrado' })
        return
      }

      if (localState === null || linkedHere === undefined) return
      const response = await fetch('http://127.0.0.1:47831/control', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ paused: !localState.paused }),
      })
      if (!response.ok) throw new Error(`El cliente local respondió HTTP ${response.status}`)
      await Swal.fire({
        icon: 'success',
        title: localState.paused ? 'Sincronización reanudada' : 'Sincronización pausada',
        text: localState.paused ? 'El motor retomará la reconciliación en su siguiente ciclo.' : 'No se iniciarán nuevos ciclos de sincronización.',
      })
    } catch (error) {
      await Swal.fire({ icon: 'error', title: 'Error de sincronización', text: getError(error) })
    }
  }

  return (
    <div
      className="file-manager"
      onClick={() => setContextMenu(null)}
      onDragEnter={(event) => {
        event.preventDefault()
        if (!event.dataTransfer.types.includes('application/x-gestorarchivos-item')) setDragging(true)
      }}
      onDragOver={(event) => {
        event.preventDefault()
        if (!event.dataTransfer.types.includes('application/x-gestorarchivos-item')) setDragging(true)
      }}
      onDragLeave={(event) => {
        if (event.currentTarget === event.target) {
          setDragging(false)
        }
      }}
      onDrop={handleDrop}
      onDragEnd={() => setDraggedItem(null)}
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
                  onDragOver={(event) => { if (draggedItem && item.id) event.preventDefault() }}
                  onDrop={(event) => { if (item.id) handleInternalDrop(event, item.id) }}
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
          <div className="file-primary-actions">
            <button className="file-action-primary" type="button" disabled={!currentFolderId || !!upload} onClick={() => void createTextFile()}>
              <span aria-hidden="true">＋</span> Nuevo archivo
            </button>
            <button type="button" disabled={!!upload} onClick={createFolder}>
              <span aria-hidden="true">＋</span> Nueva carpeta
            </button>
            <button type="button" disabled={!currentFolderId || !!upload} onClick={() => fileInput.current?.click()}>
              <span aria-hidden="true">↑</span> Subir archivos
            </button>
          </div>
          <details className="file-sync-dropdown">
            <summary title="Opciones de sincronización">Sincronización <span aria-hidden="true">⌄</span></summary>
            <div className="file-sync-dropdown-menu">
              <button
                type="button"
                onClick={(event) => {
                  event.currentTarget.closest('details')?.removeAttribute('open')
                  void fetch('http://127.0.0.1:47832/selector', { method: 'POST' })
                    .then(response => {
                      if (!response.ok) throw new Error(`Agente local: HTTP ${response.status}`)
                    })
                    .catch(() => void Swal.fire({
                      icon: 'error',
                      title: 'Selector de Windows no disponible',
                      text: 'Abre una vez el selector de Gestor Archivos desde Windows para iniciar el agente local.',
                    }))
                }}
              >Seleccionar sincronización</button>
              <button type="button" onClick={(event) => {
                event.currentTarget.closest('details')?.removeAttribute('open')
                void openSyncManagement()
              }}>Administrar sincronización</button>
            </div>
          </details>

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

        {(copyClipboard || moveClipboard || selectedCount > 0) && (
          <div className="selection-actions explorer-selection-bar">
            {moveClipboard ? (
              <>
                <span className="explorer-selection-count">{moveClipboard.folderIds.length + moveClipboard.fileIds.length} en espera de mover</span>
                <button type="button" disabled={!currentFolderId || currentFolderId === moveClipboard.sourceFolderId || moving || !!upload} onClick={() => void moveItems(moveClipboard, currentFolderId)}>Pegar aquí</button>
                <button type="button" onClick={() => setMoveClipboard(null)}>Cancelar</button>
              </>
            ) : copyClipboard ? (
              <>
                <span className="explorer-selection-count">Copiados: {copyClipboard.fileIds.length + copyClipboard.folderIds.length} elemento(s)</span>
                <button type="button" disabled={!currentFolderId || moving || !!upload} onClick={() => void pasteCopy()}>Pegar aquí</button>
                <button type="button" onClick={() => setCopyClipboard(null)}>Cancelar</button>
              </>
            ) : (
              <>
                <span className="explorer-selection-count">{selectedCount} seleccionado(s)</span>
                <button type="button" disabled={!currentFolderId || moving || !!upload} onClick={cutSelected}>Cortar</button>
                <button type="button" onClick={() => { setSelectedFolders(new Set()); setSelectedFiles(new Set()) }}>Deseleccionar</button>
              </>
            )}
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

      <div className="explorer-layout">
        <aside className="folder-sidebar" aria-label="Navegador de carpetas">
          <strong>Carpetas</strong>
          <button type="button" className="tree-root" onClick={() => navigateTo([{ id: null, nombre: 'Inicio' }])}>⌂ Inicio</button>
          <FolderTree parentId={null} depth={0} refresh={treeRefresh} activeId={currentFolderId}
            onNavigate={(path) => navigateTo([{ id: null, nombre: 'Inicio' }, ...path])}
            onMove={(event, folderId) => handleInternalDrop(event, folderId)}
            onContext={(event, folder) => showContextMenu(event, 'folder', undefined, folder)} />
        </aside>
      <div className="explorer" {...touchMenu('background')} onContextMenu={(event) => {
        if (event.target === event.currentTarget) showContextMenu(event, 'background')
      }}>
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

          <span> </span>
        </div>

        {loading ? (
          <div className="empty-state">
            Cargando...
          </div>
        ) : (
          <>
            {visibleFolders.map((folder) => (
              <div
                className={`explorer-row explorer-row-select${moveClipboard?.sourceFolderId === currentFolderId && moveClipboard.folderIds.includes(folder.id) ? " explorer-row-cut" : ""}`}
                key={folder.id}
                onContextMenu={(event) => showContextMenu(event, 'folder', undefined, folder)}
                {...touchMenu('folder', undefined, folder)}
                draggable={!!currentFolderId && !moving && !upload}
                onDragStart={(event) => handleInternalDragStart(event, 'folder', folder.id)}
                onDragOver={(event) => { if (draggedItem) { event.preventDefault(); event.stopPropagation() } }}
                onDrop={(event) => { handleInternalDrop(event, folder.id) }}
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
                    <FolderGlyph />
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

                <button type="button" className="row-more" aria-label="Acciones de carpeta" onClick={(event) => showContextMenu(event, 'folder', undefined, folder)}>⋯</button>
              </div>
            ))}

            {visibleFiles.map((file) => (
              <div
                className={`explorer-row explorer-row-select${moveClipboard?.sourceFolderId === currentFolderId && moveClipboard.fileIds.includes(file.id) ? " explorer-row-cut" : ""}`}
                key={file.id}
                onContextMenu={(event) => showContextMenu(event, 'file', file)}
                {...touchMenu('file', file)}
                draggable={!moving && !upload}
                onDragStart={(event) => handleInternalDragStart(event, 'file', file.id)}
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
                    <FileGlyph extension={file.extension} />
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

                <button type="button" className="row-more" aria-label="Acciones de archivo" onClick={(event) => showContextMenu(event, 'file', file)}>⋯</button>
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
      {contextMenu && (
        <div ref={contextMenuRef} className="file-context-menu" role="menu"
          style={{ left: Math.max(8, contextMenu.x), top: Math.max(8, contextMenu.y) }}
          onClick={(event) => event.stopPropagation()}>
          {(contextMenu.kind === 'background'
            ? [{ id: 'paste', label: 'Pegar', disabled: (!moveClipboard && !copyClipboard) || !currentFolderId }, { id: 'new-folder', label: 'Nueva carpeta' }, { id: 'new-text-file', label: 'Nuevo archivo .txt', disabled: !currentFolderId }, { id: 'upload', label: 'Subir archivos', disabled: !currentFolderId }]
            : [
              { id: 'open', label: 'Abrir', disabled: ((contextMenu.file && selectedFiles.has(contextMenu.file.id)) || (contextMenu.folder && selectedFolders.has(contextMenu.folder.id))) && selectedCount > 1 },
              ...((contextMenu.file || contextMenu.folder) ? [{ id: 'download', label: ((contextMenu.file && selectedFiles.has(contextMenu.file.id)) || (contextMenu.folder && selectedFolders.has(contextMenu.folder.id))) && selectedCount > 1 || !!contextMenu.folder ? 'Descargar ZIP' : 'Descargar', disabled: ((contextMenu.file && selectedFiles.has(contextMenu.file.id)) || (contextMenu.folder && selectedFolders.has(contextMenu.folder.id))) && selectedCount > 100 }] : []),
              ...(contextMenu.file ? [{ id: 'versions', label: 'Historial de versiones', disabled: selectedCount > 1 && selectedFiles.has(contextMenu.file.id) }] : []),
              ...(contextMenu.file && ['.txt', '.md', '.json', '.xml', '.csv', '.html', '.css', '.js', '.ts', '.log'].includes(contextMenu.file.extension.toLowerCase()) ? [{ id: 'edit', label: 'Editar', disabled: selectedFiles.has(contextMenu.file!.id) && selectedCount > 1 }] : []),
              ...((contextMenu.file || (contextMenu.folder && contextMenu.folder.carpetaPadreId)) ? [{ id: 'copy', label: ((contextMenu.file && selectedFiles.has(contextMenu.file.id)) || (contextMenu.folder && selectedFolders.has(contextMenu.folder.id))) && selectedCount > 1 ? `Copiar selección (${selectedCount})` : 'Copiar' }] : []),
              ...(currentFolderId ? [{ id: 'cut', label: ((contextMenu.file && selectedFiles.has(contextMenu.file.id)) || (contextMenu.folder && selectedFolders.has(contextMenu.folder.id))) && selectedCount > 1 ? `Cortar selección (${selectedCount})` : 'Cortar' }] : []),
              ...(!contextMenu.folder || contextMenu.folder.carpetaPadreId ? [
                { id: 'rename', label: 'Renombrar', disabled: ((contextMenu.file && selectedFiles.has(contextMenu.file.id)) || (contextMenu.folder && selectedFolders.has(contextMenu.folder.id))) && selectedCount > 1 },
                { id: 'delete', label: ((contextMenu.file && selectedFiles.has(contextMenu.file.id)) || (contextMenu.folder && selectedFolders.has(contextMenu.folder.id))) && selectedCount > 1 ? `Mover selección a papelera (${selectedCount})` : 'Mover a papelera' },
              ] : []),
            ]).map((item) => (
            <button key={item.id} type="button" role="menuitem"
              disabled={('disabled' in item && item.disabled) || moving || !!upload}
              onClick={() => contextAction(item.id)}>{item.label}</button>
          ))}
        </div>
      )}
    </div>
  )
}

function FolderTree({ parentId, depth, refresh, activeId, onNavigate, onMove, onContext, ancestors = [] }: {
  parentId: string | null
  depth: number
  refresh: number
  activeId: string | null
  onNavigate: (path: Breadcrumb[]) => void
  onMove: (event: DragEvent<HTMLElement>, id: string) => boolean
  onContext: (event: MouseEvent<HTMLElement>, folder: Folder) => void
  ancestors?: Breadcrumb[]
}) {
  const [children, setChildren] = useState<Folder[]>([])
  const [expanded, setExpanded] = useState<Set<string>>(new Set())
  useEffect(() => {
    let live = true
    const query = parentId ? `?parentId=${encodeURIComponent(parentId)}` : ''
    void apiRequest<FolderContents>(`/folders${query}`).then((result) => {
      if (live) setChildren(result.folders)
    }).catch(() => { if (live) setChildren([]) })
    return () => { live = false }
  }, [parentId, refresh])
  if (depth > 30) return null
  return <div className="folder-tree">
    {children.map((folder) => {
      const path = [...ancestors, { id: folder.id, nombre: folder.nombre }]
      const isOpen = expanded.has(folder.id)
      return <div key={folder.id}>
        <div className={`tree-entry ${activeId === folder.id ? 'tree-entry-active' : ''}`}
          style={{ paddingLeft: depth * 14 + 6 }}
          onDragOver={(event) => event.preventDefault()}
          onDrop={(event) => { onMove(event, folder.id) }}
          onContextMenu={(event) => onContext(event, folder)}>
          <button type="button" aria-label={isOpen ? 'Contraer' : 'Expandir'}
            onClick={() => setExpanded((current) => {
              const next = new Set(current)
              if (next.has(folder.id)) next.delete(folder.id)
              else next.add(folder.id)
              return next
            })}>{isOpen ? '▾' : '▸'}</button>
          <button type="button" className="tree-label" onClick={() => onNavigate(path)}><FolderGlyph /> {folder.nombre}</button>
        </div>
        {isOpen && <FolderTree parentId={folder.id} depth={depth + 1} refresh={refresh}
          activeId={activeId} onNavigate={onNavigate} onMove={onMove}
          onContext={onContext} ancestors={path} />}
      </div>
    })}
  </div>
}

function FolderGlyph() {
  return <svg className="file-glyph folder-glyph" viewBox="0 0 24 24" width="23" height="23" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinejoin="round" aria-hidden="true"><path d="M3 7a2 2 0 0 1 2-2h5l2 2h7a2 2 0 0 1 2 2v10H3z" /></svg>
}

function FileGlyph({ extension }: { extension: string }) {
  const ext = extension.toLowerCase()
  const type = ext === '.pdf' ? 'pdf' : ['.jpg','.jpeg','.png','.gif','.webp'].includes(ext) ? 'image' : ['.doc','.docx'].includes(ext) ? 'word' : ['.xls','.xlsx','.csv'].includes(ext) ? 'sheet' : ['.ppt','.pptx'].includes(ext) ? 'slide' : ['.zip','.rar','.7z'].includes(ext) ? 'archive' : 'generic'
  return <svg className={`file-glyph file-glyph-${type}`} viewBox="0 0 24 24" width="23" height="23" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinejoin="round" aria-hidden="true"><path d="M6 3h8l5 5v13H6zM14 3v5h5" /><path d="M9 13h6M9 17h6" /></svg>
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
