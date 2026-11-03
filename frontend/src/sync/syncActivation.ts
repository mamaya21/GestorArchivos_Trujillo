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

export const CLIENT_INSTALLER_URL = import.meta.env.VITE_CLIENT_INSTALLER_URL as string | undefined

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

export async function activateSyncOnThisComputer(reuseDeviceId?: string, reuseDeviceName?: string): Promise<void> {
  await ensureLocalSyncReady()

  const registration =
    await apiRequest<RegisterSyncDeviceResponse>(
      '/sync/devices/register',
      {
        method: 'POST',
        body: JSON.stringify({
          name: reuseDeviceName ?? (window.navigator.userAgent.includes('Windows')
            ? `Windows - ${window.location.hostname}`
            : window.location.hostname),
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