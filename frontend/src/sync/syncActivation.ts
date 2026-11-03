import { apiRequest } from '../api/client'

const LOCAL_ACTIVATION_URL =
  'http://127.0.0.1:47831/activate'

type RegisterSyncDeviceResponse = {
  deviceId: string
  deviceName: string
  rootFolderId: string
  rootFolderName: string
  token: string
}

export function isWindowsDesktop(): boolean {
  const ua = navigator.userAgent
  return /Windows NT/i.test(ua) && !/Android|iPhone|iPad|Mobile/i.test(ua)
}

export const CLIENT_INSTALLER_URL = (import.meta.env.VITE_CLIENT_INSTALLER_URL as string | undefined) || '/instaladores/GestorArchivos-Instalador-x64.exe'

export async function checkSelectorReady(): Promise<boolean> {
  try {
    const response = await fetch('http://127.0.0.1:47832/selector', {
      method: 'GET',
      cache: 'no-store',
      signal: AbortSignal.timeout(4000),
    })
    return response.status === 204
  } catch {
    return false
  }
}

export async function ensureLocalSyncReady(): Promise<void> {
  if (!isWindowsDesktop()) {
    throw new Error('La sincronización local solo está disponible en computadoras Windows.')
  }

  let response: Response
  try {
    response = await fetch('http://127.0.0.1:47831/control', {
      method: 'GET',
      cache: 'no-store',
      signal: AbortSignal.timeout(4000),
    })
  } catch {
    throw new Error('No se detectó el cliente de sincronización. Instala e inicia Gestor de Archivos para Windows antes de vincular este equipo.')
  }

  if (!response.ok) {
    throw new Error('El cliente local de sincronización no está disponible. Comprueba que esté iniciado.')
  }

  let state: unknown
  try {
    state = await response.json()
  } catch {
    throw new Error('El cliente local devolvió una respuesta inválida.')
  }
  if (!state || typeof state !== 'object' || !('paused' in state) ||
      typeof (state as { paused: unknown }).paused !== 'boolean') {
    throw new Error('No se pudo verificar el estado del servicio de sincronización.')
  }

  if (!await checkSelectorReady()) {
    throw new Error('No se detectó el selector de carpetas. Inicia el selector de Gestor de Archivos antes de vincular el equipo.')
  }
}

export async function activateSyncOnThisComputer(reuseDeviceId?: string): Promise<void> {
  await ensureLocalSyncReady()

  const localControl = await fetch('http://127.0.0.1:47831/control', {
    method: 'GET', cache: 'no-store', signal: AbortSignal.timeout(4000),
  })
  if (!localControl.ok) throw new Error('No se pudo identificar el equipo local.')
  const localState: unknown = await localControl.json()
  const hostName = localState && typeof localState === 'object' && 'hostName' in localState &&
    typeof (localState as { hostName: unknown }).hostName === 'string'
    ? (localState as { hostName: string }).hostName.trim() : ''
  if (!hostName) throw new Error('Actualiza el cliente de sincronización para registrar el nombre real del equipo.')

  const linkedDeviceId = localState && typeof localState === 'object' && 'deviceId' in localState &&
    typeof (localState as { deviceId: unknown }).deviceId === 'string'
    ? (localState as { deviceId: string }).deviceId : ''
  if (linkedDeviceId && linkedDeviceId !== reuseDeviceId) {
    const previous = await apiRequest<{ active: boolean }>(
      `/sync/devices/${encodeURIComponent(linkedDeviceId)}/registration-state`,
    )
    if (previous.active) {
      throw new Error('Este equipo sigue vinculado a otra cuenta. Desvincúlalo desde esa cuenta antes de registrarlo.')
    }
    const deactivate = await fetch('http://127.0.0.1:47831/deactivate', { method: 'POST' })
    if (!deactivate.ok) {
      throw new Error('No se pudo limpiar la vinculación anterior del equipo.')
    }
  }

  const registration =
    await apiRequest<RegisterSyncDeviceResponse>(
      '/sync/devices/register',
      {
        method: 'POST',
        body: JSON.stringify({
          name: hostName,
          reuseDeviceId,
          operatingSystem: 'Windows',
        }),
      },
    )

  const response = await fetch(LOCAL_ACTIVATION_URL, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ ...registration, startFresh: !reuseDeviceId }),
  })

  if (!response.ok) {
    throw new Error(
      `No se pudo activar el cliente local. HTTP ${response.status}`,
    )
  }
}