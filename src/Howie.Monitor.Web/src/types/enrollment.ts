export interface EnrollmentProgress { state: string; computerName: string | null; registered: boolean; heartbeatReceived: boolean; dataStatus: string; sampledAt: string | null; detail: string | null }
export interface EnrollmentOverview { currentDeviceId: string | null; currentComputerName: string | null; pending: EnrollmentProgress | null; codeExpiresAt: string | null }
export interface EnrollmentCode { code: string; expiresAt: string; previousComputerName: string | null }
export interface SetupDownload { available: boolean; version: string; fileName: string | null; bytes: number | null; sha256: string | null }
