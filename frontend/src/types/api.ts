export interface CurrentUser {
  id: string
  userName: string
  email: string
  nombre: string
  apellido: string
  ultimoAcceso: string | null
}

export interface Folder {
  selectorManaged?: boolean
  deviceContainer?: boolean
  id: string
  nombre: string
  carpetaPadreId: string | null
  fechaCreacion: string
  fechaModificacion: string | null
}

export interface FileItem {
  selectorManaged?: boolean
  id: string
  nombre: string
  extension: string
  tipoContenido: string
  tamanoBytes: number
  fechaCreacion: string
  fechaModificacion: string | null
}

export interface FolderContents {
  parentId: string | null
  folders: Folder[]
  files: FileItem[]
}

export interface UploadInitializeResponse {
  uploadId: string
  chunkSize: number
  expectedChunkCount: number
  expiresAtUtc: string
}

export interface UploadStatusResponse {
  uploadId: string
  fileSize: number
  chunkSize: number
  expectedChunkCount: number
  expiresAtUtc: string
  receivedChunks: number[]
}