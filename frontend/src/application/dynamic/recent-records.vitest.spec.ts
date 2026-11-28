import { afterEach, describe, expect, it, vi } from 'vitest';
import { readRecentRecords, recordRecentRecord } from './recent-records';

afterEach(() => { vi.restoreAllMocks(); localStorage.clear(); });
const ids = (userId = 'u1') => readRecentRecords(userId).map((r) => `${r.moduleKey}/${r.recordId}`);

describe('recent records', () => {
  it('最新的在前、同一筆去重、只存 10 筆', () => {
    for (let i = 1; i <= 12; i++) recordRecentRecord('u1', 'm', i);
    recordRecentRecord('u1', 'm', 5);
    expect(ids()).toEqual(['m/5', 'm/12', 'm/11', 'm/10', 'm/9', 'm/8', 'm/7', 'm/6', 'm/4', 'm/3']);
  });
  it('同一個 id 在不同模組是不同筆', () => {
    recordRecentRecord('u1', 'a', 1);
    recordRecentRecord('u1', 'b', 1);
    expect(ids()).toEqual(['b/1', 'a/1']);
  });
  it('只存模組鍵與單據 id，不存標題', () => {
    recordRecentRecord('u1', 'orders', 7);
    expect(JSON.parse(localStorage.getItem('formwright:recent-records:u1')!)).toEqual([{ moduleKey: 'orders', recordId: 7 }]);
  });
  it('依使用者分開；沒有使用者、模組鍵或 id 不合法就不記', () => {
    recordRecentRecord('u1', 'a', 1);
    recordRecentRecord('', 'a', 2);
    recordRecentRecord('u1', '', 2);
    recordRecentRecord('u1', 'a', Number.NaN);
    expect(ids('u1')).toEqual(['a/1']);
    expect(ids('u2')).toEqual([]);
    expect(ids('')).toEqual([]);
  });
  it('壞資料當空，壞掉的項目與多餘欄位丟掉', () => {
    localStorage.setItem('formwright:recent-records:u1', '{"not":"array"}');
    expect(ids()).toEqual([]);
    localStorage.setItem('formwright:recent-records:u1', 'not json');
    expect(ids()).toEqual([]);
    localStorage.setItem('formwright:recent-records:u1', '[1,null,{"moduleKey":"a"},{"moduleKey":"a","recordId":"2"},{"moduleKey":"a","recordId":3,"title":"x"}]');
    expect(readRecentRecords('u1')).toEqual([{ moduleKey: 'a', recordId: 3 }]);
  });
  it('storage 不可用時不拋錯', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked'); });
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked'); });
    expect(() => recordRecentRecord('u1', 'a', 1)).not.toThrow();
    expect(ids()).toEqual([]);
  });
});
