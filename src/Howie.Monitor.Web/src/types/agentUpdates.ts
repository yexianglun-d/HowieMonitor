export interface AgentBuild { version: string; buildId: string; updateProtocol: number }
export interface AgentRelease { version: string; buildId: string; protocol: number; payloadSha256: string; sha256: string; bytes: number }
export interface AgentUpdateOperation {
  ticket: { id: string; deviceId: string; release: AgentRelease; createdAt: string; expiresAt: string }
  phase: string; message: string | null; updatedAt: string
}
export interface AgentUpdateStatus {
  computerId: string; current: AgentBuild | null; sessionId: string; release: AgentRelease | null
  canUpdate: boolean; reason: string | null; operation: AgentUpdateOperation | null
}
