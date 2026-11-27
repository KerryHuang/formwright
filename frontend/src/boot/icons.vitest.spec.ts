import { mount } from '@vue/test-utils';
import * as quasar from 'quasar';
import { describe, expect, it } from 'vitest';
import config from '../../quasar.config.ts?raw';
import icons from './icons';

describe('boot/icons', () => {
  it('empty q-table rows show a neutral icon instead of the warning triangle', () => {
    const Host = {
      components: { QTable: quasar.QTable },
      template: '<q-table :rows="[]" :columns="[]" no-data-label="沒有資料" />',
    };
    const w = mount(Host, {
      global: { plugins: [[quasar.Quasar, { components: quasar }], { install: (app) => void icons({ app } as never) }] },
    });
    const bottom = w.get('.q-table__bottom--nodata');
    expect(bottom.text()).toContain('沒有資料');
    expect(bottom.get('.q-table__bottom-nodata-icon').text()).toBe('inbox');
  });

  it('is registered as a boot file', () => {
    expect(/boot: \[[^\]]*'icons'/.test(config)).toBe(true);
  });
});
