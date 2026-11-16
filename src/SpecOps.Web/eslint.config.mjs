// ESLint flat config for the browser JavaScript in this project.
// Formatting is Prettier's job; ESLint only checks for bugs and bad practices.
import js from '@eslint/js';
import globals from 'globals';

const strictRules = {
  eqeqeq: ['error', 'always'],
  'no-var': 'error',
  'prefer-const': 'error',
  'no-restricted-properties': [
    'error',
    { object: 'document', property: 'execCommand', message: 'Deprecated; use the Clipboard API.' },
  ],
  'no-unused-vars': ['error', { args: 'after-used', caughtErrors: 'none' }],
};

export default [
  {
    // Build output and generated bundles (wwwroot/lib/editor is produced by Client/build-editor.mjs).
    ignores: ['bin/**', 'obj/**', 'node_modules/**', 'wwwroot/css/**', 'wwwroot/lib/**'],
  },
  js.configs.recommended,
  {
    // Classic scripts loaded by the page.
    files: ['wwwroot/js/**/*.js'],
    languageOptions: {
      ecmaVersion: 'latest',
      sourceType: 'script',
      globals: { ...globals.browser, Blazor: 'readonly' },
    },
    linterOptions: { reportUnusedDisableDirectives: 'error' },
    rules: { ...strictRules, 'no-implicit-globals': 'error' },
  },
  {
    // ES modules: Blazor's collocated component scripts (*.razor.js) and the editor module source.
    files: ['Components/**/*.js', 'Client/editor/**/*.js'],
    languageOptions: {
      ecmaVersion: 'latest',
      sourceType: 'module',
      globals: { ...globals.browser, Blazor: 'readonly' },
    },
    linterOptions: { reportUnusedDisableDirectives: 'error' },
    rules: strictRules,
  },
  {
    // Build scripts run by Node.
    files: ['Client/*.mjs', 'eslint.config.mjs'],
    languageOptions: { sourceType: 'module', globals: globals.node },
    rules: strictRules,
  },
];
