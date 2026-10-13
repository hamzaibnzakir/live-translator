import js from '@eslint/js';
import tseslint from 'typescript-eslint';
import globals from 'globals';

export default tseslint.config(
  { ignores: ['dist/**', 'dist-test/**', 'node_modules/**', 'static/vendor/**', 'test-results/**', 'playwright-report/**', 'tests/.cache/**', 'tests/e2e/fixtures/dist/**', 'reports/**'] },
  js.configs.recommended,
  ...tseslint.configs.recommended,
  {
    languageOptions: { globals: { ...globals.browser, ...globals.webextensions, ...globals.node } },
    rules: {
      '@typescript-eslint/no-unused-vars': ['error', { argsIgnorePattern: '^_', varsIgnorePattern: '^_' }],
      '@typescript-eslint/no-non-null-assertion': 'off',
      'no-empty': ['error', { allowEmptyCatch: true }],
    },
  },
  // Playwright fixtures use `async ({}, use)` by convention.
  { files: ['tests/**/*.ts'], rules: { 'no-empty-pattern': 'off', '@typescript-eslint/no-explicit-any': 'off' } },
);
