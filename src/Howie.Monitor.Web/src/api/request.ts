let csrfToken = ''
export class ApiError extends Error { constructor(message: string, public status: number) { super(message) } }

export async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  if (init.body) headers.set('Content-Type', 'application/json')
  if (init.method && init.method !== 'GET') headers.set('X-CSRF-TOKEN', csrfToken)
  const response = await fetch(path, { ...init, headers, credentials: 'same-origin', cache: 'no-store' })
  if (!response.ok) {
    const body = await response.json().catch(() => ({})) as { error?: string; detail?: string }
    throw new ApiError(body.error ?? body.detail ?? `请求失败（${response.status}）`, response.status)
  }
  return response.status === 204 ? undefined as T : response.json() as Promise<T>
}
export async function refreshCsrf() { csrfToken = (await request<{ token: string }>('/api/auth/csrf')).token }
