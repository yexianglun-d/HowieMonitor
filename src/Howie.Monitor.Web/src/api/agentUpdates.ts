import { refreshCsrf, request } from './request'
import type { AgentUpdateOperation, AgentUpdateStatus } from '../types/agentUpdates'

export const getAgentUpdateStatus = (computerId: string, signal: AbortSignal) =>
  request<AgentUpdateStatus>(`/api/agent-updates/status?${new URLSearchParams({ computerId })}`, { signal })
export async function createAgentUpdate(computerId: string, password: string, expectedSessionId: string, sha256: string, signal: AbortSignal) {
  await refreshCsrf()
  return request<AgentUpdateOperation>('/api/agent-updates', { method: 'POST', body: JSON.stringify({ computerId, password, expectedSessionId, sha256 }), signal })
}
