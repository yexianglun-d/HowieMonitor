export interface TerminalStatus { connected: boolean; available: boolean; account: string | null; reason: string | null; activeSessionId: string | null }
export interface TerminalSession { sessionId: string; connectBefore: string }
export interface TerminalFrame { type: string; data?: string; columns?: number; rows?: number; account?: string; userSid?: string; administrator?: boolean }
export type TerminalPhase = 'idle' | 'connecting' | 'ready' | 'closed' | 'error'
