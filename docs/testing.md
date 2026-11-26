# Tests

```bash
dotnet test
```

runs everything:

| Project               | What                                                                                                                                                                                          |
| --------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `tests/SpecOps.Tests` | Unit and integration tests (xUnit): the script runner, validation, sign-in, HTTP endpoints, and Blazor components (bUnit).                                                                    |
| `tests/SpecOps.E2E`   | Browser tests (xUnit + [Playwright](https://playwright.dev/dotnet/)): starts SpecOps on a free port in the Development environment (dev sign-in, sample scripts) and drives it with Chromium. |

Run one project: `dotnet test --project tests/SpecOps.E2E`.

## Browser tests

- **Chromium** is downloaded automatically on the first run (into Playwright's cache, not the repo).
- **Linux** also needs the browser's system libraries, once:

  ```bash
  dotnet build tests/SpecOps.E2E
  pwsh tests/SpecOps.E2E/bin/Debug/net11.0/playwright.ps1 install --with-deps chromium
  ```

- **Watch them run**: set `SPECOPS_E2E_HEADED=1` to open a visible browser window.
- **Slow machine**: set `SPECOPS_E2E_CPU_THROTTLE=6` to run the browser that many times slower, to reproduce timing
  problems that only show up on CI runners.
- The tests cover what only a browser can: sign-in, running and cancelling scripts with live output, the Terminal's
  editor (Monaco) and Results view, the theme toggle, and that pages load without script errors or CSP violations.

## Client code

The browser code is TypeScript in `src/SpecOps.Web/Client` (compiled by esbuild during the build). From
`src/SpecOps.Web`:

```bash
npm run typecheck   # tsc, strict
npm run lint        # ESLint with type-aware rules
```
