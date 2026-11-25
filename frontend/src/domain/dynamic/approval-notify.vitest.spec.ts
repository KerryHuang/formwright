import { beforeAll, describe, expect, it } from 'vitest';
import cases from '../../../../shared/approval-notify-cases.json';
import { setLocaleInfos, setLocaleSettings } from 'src/i18n/locale-settings';
import { parseNotifyData, renderNotify, type ApprovalNotifyData } from './approval-notify';

const texts = cases.texts;

beforeAll(() => {
  setLocaleInfos(['zh-TW', 'en-US', 'th', 'vi'].map((code) => ({ code, nativeName: code, quasarLang: null, fallbackCode: null, uiHash: '' })) as never);
  setLocaleSettings({ defaultLocale: 'zh-TW' });
});

describe('renderNotify（共用案例 shared/approval-notify-cases.json）', () => {
  for (const c of cases.cases) {
    it(c.name, () => {
      expect(renderNotify(c.data as unknown as ApprovalNotifyData, c.locale, texts)).toEqual(c.expected);
    });
  }
});

describe('parseNotifyData', () => {
  it('不是物件或版本不對回 null', () => {
    expect(parseNotifyData(null)).toBeNull();
    expect(parseNotifyData('x')).toBeNull();
    expect(parseNotifyData({ v: 2 })).toBeNull();
  });
  it('結構完整時原樣回傳', () => {
    const d = cases.cases[0]!.data;
    expect(parseNotifyData(d)).toEqual(d);
  });
});
