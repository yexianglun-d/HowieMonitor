import { request, refreshCsrf } from './request'
import type { EnrollmentCode, EnrollmentOverview, SetupDownload } from '../types/enrollment'

export const getEnrollment = (signal: AbortSignal) => request<EnrollmentOverview>('/api/enrollment', { signal })
export const getSetupDownload = (signal: AbortSignal) => request<SetupDownload>('/api/setup', { signal })
export async function createEnrollmentCode(expectedDeviceId: string | null, confirmed: boolean, signal: AbortSignal) {
  await refreshCsrf()
  return request<EnrollmentCode>('/api/enrollment/code', { method: 'POST', body: JSON.stringify({ expectedDeviceId, confirmReplacement: confirmed }), signal })
}
export async function revokeEnrollmentCode(signal: AbortSignal) {
  await refreshCsrf()
  await request('/api/enrollment/code', { method: 'DELETE', signal })
}
