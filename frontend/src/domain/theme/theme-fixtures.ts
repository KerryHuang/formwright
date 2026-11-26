import type { MeTheme } from './theme-model';

/** 測試用：沒有任何個人設定、全部取出廠值的 Me.theme。 */
export const FACTORY_ME_THEME: MeTheme = {
  mode: null, style: null, effectiveMode: 'system', effectiveStyle: 'signature',
  defaultMode: 'system', defaultStyle: 'signature', canSave: true,
};
