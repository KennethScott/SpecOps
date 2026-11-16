// ESLint flat config for the browser TypeScript (Client/) and the Node build scripts.
// Formatting is Prettier's job; ESLint checks for bugs and bad practices, with type information for the .ts files.
import js from '@eslint/js';
import globals from 'globals';
import tseslint from 'typescript-eslint';

const strictRules = {
  eqeqeq: ['error', 'always'],
  'no-var': 'error',
  'prefer-const': 'error',
  'no-restricted-properties': [
    'error',
    { object: 'document', property: 'execCommand', message: 'Deprecated; use the Clipboard API.' },
  ],
};

export default tseslint.config(
  {
    // Build output and generated bundles (wwwroot/js and wwwroot/lib/editor are produced by Client/build-client.mjs).
    ignores: ['bin/**', 'obj/**', 'node_modules/**', 'wwwroot/**'],
  },
  {
    linterOptions: { reportUnusedDisableDirectives: 'error' },
  },
  js.configs.recommended,
  {
    // Browser code: strict, type-aware rules (tsconfig.json).
    files: ['Client/**/*.ts'],
    extends: [tseslint.configs.strictTypeChecked, tseslint.configs.stylisticTypeChecked],
    languageOptions: {
      globals: globals.browser,
      parserOptions: { projectService: true, tsconfigRootDir: import.meta.dirname },
    },
    rules: {
      ...strictRules,
      '@typescript-eslint/explicit-module-boundary-types': 'error',
      '@typescript-eslint/consistent-type-imports': 'error',
    },
  },
  {
    // Ambient declarations: interfaces merge with the DOM's, so `type` aliases can't replace them.
    files: ['Client/**/*.d.ts'],
    rules: { '@typescript-eslint/consistent-type-definitions': 'off' },
  },
  {
    // Build scripts run by Node.
    files: ['Client/*.mjs', 'eslint.config.mjs'],
    languageOptions: { sourceType: 'module', globals: globals.node },
    rules: { ...strictRules, 'no-unused-vars': ['error', { args: 'after-used', caughtErrors: 'none' }] },
  },
);
