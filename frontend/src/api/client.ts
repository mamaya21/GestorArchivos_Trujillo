const API_BASE = '/api'

export class ApiError extends Error {
  status: number

  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
}

export async function apiRequest<T>(
  path: string,
  options: RequestInit = {},
): Promise<T> {
  const response = await fetch(`${API_BASE}${path}`, {
    ...options,
    credentials: 'include',
    headers: {
      ...options.headers,
      ...(options.body &&
      !(options.body instanceof FormData) &&
      !(options.body instanceof Blob)
        ? { 'Content-Type': 'application/json' }
        : {}),
    },
  })

  if (!response.ok) {
    let message = `Error HTTP ${response.status}`

    try {
      const body = await response.json()

      if (body?.message) {
        message = body.message
      }
    } catch {
      // Respuesta sin JSON.
    }

    throw new ApiError(message, response.status)
  }

  if (
    response.status === 204 ||
    response.status === 205
  ) {
    return undefined as T
  }

  const text = await response.text()

  if (!text) {
    return undefined as T
  }

  return JSON.parse(text) as T
}

export async function apiRaw(
  path: string,
  options: RequestInit = {},
): Promise<Response> {
  const response = await fetch(`${API_BASE}${path}`, {
    ...options,
    credentials: 'include',
  })

  if (!response.ok) {
    let message = `Error HTTP ${response.status}`

    try {
      const body = await response.json()

      if (body?.message) {
        message = body.message
      }
    } catch {
      // Respuesta sin JSON.
    }

    throw new ApiError(message, response.status)
  }

  return response
}