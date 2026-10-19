export const pages = ['overview', 'detail', 'processes', 'history', 'hardware', 'notifications', 'enrollment'] as const
export type Page = typeof pages[number]

export function readLocation(hash: string) {
  const [name, query = ''] = hash.replace(/^#\/?/, '').split('?')
  return { page: pages.includes(name as Page) ? name as Page : 'overview', query: new URLSearchParams(query) }
}

export function locationHash(page: Page, query: URLSearchParams) {
  return '#' + page + (query.size ? '?' + query.toString() : '')
}

export function replaceQuery(values: Record<string, string | null>) {
  const { page, query } = readLocation(window.location.hash)
  for (const [key, value] of Object.entries(values)) {
    if (value) query.set(key, value)
    else query.delete(key)
  }
  window.history.replaceState(null, '', locationHash(page, query))
}
