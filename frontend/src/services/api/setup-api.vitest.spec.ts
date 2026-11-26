import { beforeEach, describe, expect, it } from 'vitest';
import type { InternalAxiosRequestConfig } from 'axios';
import { setupApi, setupHttp, SetupValidationError } from './setup-api';

type Reply = { status: number; data?: unknown } | 'network';
let reply: Reply;
let last: InternalAxiosRequestConfig | undefined;

beforeEach(() => {
  last = undefined;
  setupHttp.defaults.adapter = async (config) => {
    last = config;
    if (reply === 'network') return Promise.reject({ config, code: 'ERR_NETWORK', message: 'Network Error' });
    if (reply.status >= 400) return Promise.reject({ config, response: { status: reply.status, data: reply.data, headers: {}, config } });
    return { data: reply.data ?? {}, status: reply.status, statusText: 'OK', headers: {}, config };
  };
});

const rejection = async (p: Promise<unknown>) => p.then(() => undefined, (e: unknown) => e);

describe('getStatus', () => {
  it('200 → pending', async () => {
    reply = { status: 200, data: { setupPending: true } };
    expect(await setupApi.getStatus()).toBe('pending');
    expect(last?.url).toBe('setup/status');
  });
  it('404 → notInSetup', async () => {
    reply = { status: 404 };
    expect(await setupApi.getStatus()).toBe('notInSetup');
  });
  it('網路錯誤 reject', async () => {
    reply = 'network';
    expect(await rejection(setupApi.getStatus())).toMatchObject({ status: 0, code: 'network' });
  });
  it('reject shape matches ApiError：503 maintenance', async () => {
    reply = { status: 503, data: { code: 'maintenance' } };
    const e = await rejection(setupApi.getStatus());
    expect(e).toMatchObject({ status: 503, code: 'maintenance' });
    expect((e as { isAxiosError?: boolean }).isAxiosError).toBeUndefined();
  });
});

describe('isReady', () => {
  it('200 → true', async () => {
    reply = { status: 200, data: { status: 'ok' } };
    expect(await setupApi.isReady()).toBe(true);
    expect(last?.url).toBe('health/ready');
  });
  it('503 與網路錯誤 → false', async () => {
    reply = { status: 503, data: { status: 'setupRequired' } };
    expect(await setupApi.isReady()).toBe(false);
    reply = 'network';
    expect(await setupApi.isReady()).toBe(false);
  });
});

describe('verify', () => {
  it('204 → true，403 → false', async () => {
    reply = { status: 204 };
    expect(await setupApi.verify('123456')).toBe(true);
    expect(JSON.parse(String(last?.data))).toEqual({ code: '123456' });
    reply = { status: 403, data: { code: 'setupCodeInvalid' } };
    expect(await setupApi.verify('000000')).toBe(false);
  });
  it('其他錯誤 reject', async () => {
    reply = 'network';
    expect(await rejection(setupApi.verify('1'))).toMatchObject({ code: 'network' });
  });
});

describe('runChecks', () => {
  it('送 X-Setup-Code 標頭與草稿', async () => {
    reply = { status: 200, data: { items: [{ key: 'dataDirectory', ok: true, path: '/d' }], ok: true } };
    const r = await setupApi.runChecks('123456', { provider: 'postgres', connectionString: 'Host=x' });
    expect(r.ok).toBe(true);
    expect(last?.url).toBe('setup/checks');
    expect(last?.headers.get('X-Setup-Code')).toBe('123456');
    expect(JSON.parse(String(last?.data))).toEqual({ provider: 'postgres', connectionString: 'Host=x' });
  });
});

describe('complete', () => {
  const body = { provider: 'sqlite' as const, adminPassword: 'p', siteName: 's', backupRoot: '/b' };
  it('200 → resolve', async () => {
    reply = { status: 200, data: { restarting: true } };
    await expect(setupApi.complete('123456', body)).resolves.toBeUndefined();
    expect(last?.url).toBe('setup/complete');
    expect(last?.headers.get('X-Setup-Code')).toBe('123456');
  });
  it('422 checksFailed → SetupValidationError 帶 checks', async () => {
    reply = { status: 422, data: { code: 'checksFailed', items: [{ key: 'backupDirectory', ok: false, problem: 'notWritable' }] } };
    const e = await rejection(setupApi.complete('1', body));
    expect(e).toBeInstanceOf(SetupValidationError);
    expect((e as SetupValidationError).checks?.[0].problem).toBe('notWritable');
    expect((e as SetupValidationError).errors).toBeUndefined();
  });
  it('422 invalid → SetupValidationError 帶 errors', async () => {
    reply = { status: 422, data: { code: 'invalid', errors: { adminPassword: [{ code: 'passwordTooShort', params: { min: 8 } }] } } };
    const e = (await rejection(setupApi.complete('1', body))) as SetupValidationError;
    expect(e).toBeInstanceOf(SetupValidationError);
    expect(e.errors?.adminPassword[0]).toEqual({ code: 'passwordTooShort', params: { min: 8 } });
  });
  it('其他錯誤丟 ApiError', async () => {
    reply = { status: 403, data: { code: 'setupCodeInvalid' } };
    expect(await rejection(setupApi.complete('1', body))).toMatchObject({ status: 403, code: 'setupCodeInvalid' });
  });
});

describe('getMaintenance', () => {
  const info = { reason: 'preUpgradeBackupFailed', errorCode: 'destinationUnwritable', version: '0.0.2', previousVersion: '0.0.1' };
  it('200 → 物件', async () => {
    reply = { status: 200, data: info };
    expect(await setupApi.getMaintenance()).toEqual(info);
    expect(last?.url).toBe('maintenance');
  });
  it('404 → null', async () => {
    reply = { status: 404 };
    expect(await setupApi.getMaintenance()).toBeNull();
  });
  it('網路錯誤 reject', async () => {
    reply = 'network';
    expect(await rejection(setupApi.getMaintenance())).toMatchObject({ status: 0 });
  });
  it('reject shape matches ApiError：503 setupRequired', async () => {
    reply = { status: 503, data: { code: 'setupRequired' } };
    expect(await rejection(setupApi.getMaintenance())).toMatchObject({ status: 503, code: 'setupRequired' });
  });
});
