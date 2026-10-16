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

export async function activateSyncOnThisComputer(reuseDeviceId?: string, reuseDeviceName?: string): Promise<void> {
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
    body: JSON.stringify(registration),
  })

  if (!response.ok) {
    throw new Error(
      `No se pudo activar el cliente local. HTTP ${response.status}`,
    )
  }
}