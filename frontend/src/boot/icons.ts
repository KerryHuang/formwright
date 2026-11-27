import { defineBoot } from '#q-app';

/** q-table 無資料列的預設圖示是警告三角，看起來像出錯：全站換成中性圖示（設計 2026-11-27 §1.3）。 */
export default defineBoot(({ app }) => {
  app.config.globalProperties.$q.iconSet.table.warning = 'inbox';
});
