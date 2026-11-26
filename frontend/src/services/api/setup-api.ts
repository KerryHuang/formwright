import axios from 'axios';
import { toApiError } from 'src/services/api/http';

/** 設定精靈與維護頁專用：不經 http.ts 的 401／403 處理與 X-Locale（設定模式沒有 session，也沒有後端訊息可翻）。 */
export const setupHttp = axios.create({ baseURL: '/api' });

export interface SetupDraft {
  provider: 'sqlite' | 'postgres';
  connectionString?: string;
  backupRoot?: string;
}

export interface SetupCheckItem {
  key: string;
  ok: boolean;
  path?: string;
  problem?: string;
  params?: Record<string, string>;
}

export interface SetupChecksResult {
  items: SetupCheckItem[];
  ok: boolean;
}

export interface SetupCompleteBody extends SetupDraft {
  adminPassword: string;
  siteName: string;
}

export interface SetupFieldError {
  code: string;
  params?: Record<string, unknown>;
}

/** 422：`invalid` 帶欄位錯誤（errors）、`checksFailed` 帶重跑後的檢查結果（checks）。 */
export class SetupValidationError extends Error {
  constructor(
    public readonly errors?: Record<string, SetupFieldError[]>,
    public readonly checks?: SetupCheckItem[],
  ) {
    super('setup validation failed');
  }
}

export interface MaintenanceInfo {
  reason: 'preUpgradeBackupFailed' | 'backupNotConfigured';
  errorCode: string | null;
  version: string;
  previousVersion: string | null;
}

const codeHeader = (code: string) => ({ headers: { 'X-Setup-Code': code } });
const fail = (e: unknown): never => { throw toApiError(e); };
const statusOf = (e: unknown): number | undefined => (e as { response?: { status?: number } } | null)?.response?.status;

export const setupApi = {
  async getStatus(): Promise<'pending' | 'notInSetup'> {
    try {
      await setupHttp.get('setup/status');
      return 'pending';
    } catch (e) {
      if (statusOf(e) === 404) return 'notInSetup';
      return fail(e);
    }
  },

  async isReady(): Promise<boolean> {
    try {
      await setupHttp.get('health/ready');
      return true;
    } catch {
      return false;
    }
  },

  async verify(code: string): Promise<boolean> {
    try {
      await setupHttp.post('setup/verify', { code });
      return true;
    } catch (e) {
      if (statusOf(e) === 403) return false;
      return fail(e);
    }
  },

  runChecks(code: string, draft?: SetupDraft): Promise<SetupChecksResult> {
    return setupHttp.post<SetupChecksResult>('setup/checks', draft ?? {}, codeHeader(code)).then((r) => r.data).catch(fail);
  },

  async complete(code: string, body: SetupCompleteBody): Promise<void> {
    try {
      await setupHttp.post('setup/complete', body, codeHeader(code));
    } catch (e) {
      const data = (e as { response?: { data?: { code?: string; errors?: Record<string, SetupFieldError[]>; items?: SetupCheckItem[] } } })?.response?.data;
      if (statusOf(e) === 422 && data?.code === 'checksFailed') throw new SetupValidationError(undefined, data.items ?? []);
      if (statusOf(e) === 422 && data?.code === 'invalid') throw new SetupValidationError(data.errors ?? {});
      fail(e);
    }
  },

  async getMaintenance(): Promise<MaintenanceInfo | null> {
    try {
      return (await setupHttp.get<MaintenanceInfo>('maintenance')).data;
    } catch (e) {
      if (statusOf(e) === 404) return null;
      return fail(e);
    }
  },
};
