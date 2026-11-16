// ESLint flat config for the browser JavaScript in this project.
// Formatting is Prettier's job; ESLint only checks for bugs and bad practices.
import js from '@eslint/js';
import globals from 'globals';

export default [
  {
    ignores: ['bin/**', 'obj/**', 'node_modules/**', 'wwwroot/css/**'],
  },
  js.configs.recommended,
  {
    files: ['wwwroot/js/**/*.js', 'Components/**/*.js'],
    languageOptions: {
      ecmaVersion: 'latest',
      sourceType: 'script',
      globals: {
        ...globals.browser,
        Blazor: 'readonly',
      },
    },
    linterOptions: {
      reportUnusedDisableDirectives: 'error',
    },
    rules: {
      eqeqeq: ['error', 'always'],
      'no-var': 'error',
      'prefer-const': 'error',
      'no-implicit-globals': 'error',
      'no-restricted-properties': [
        'error',
        { object: 'document', property: 'execCommand', message: 'Deprecated; use the Clipboard API.' },
      ],
      'no-unused-vars': ['error', { args: 'after-used', caughtErrors: 'none' }],
    },
  },
  {
    // Blazor's collocated component scripts (*.razor.js) are ES modules.
    files: ['Components/**/*.razor.js'],
    languageOptions: { sourceType: 'module' },
  },
  {
    files: ['eslint.config.mjs'],
    languageOptions: { sourceType: 'module', globals: globals.node },
  },
];
