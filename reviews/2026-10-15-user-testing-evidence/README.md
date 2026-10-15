# User testing evidence

Tested source: `5764c4afad389be7e0c7106791ff238a8aa760e9`.

See the [report](../2026-10-15-user-testing.md) for severity, steps, and limits.

- `results.json`: five reproduced findings and the passing Back navigation control.
- `reproduce.mjs`: the focused Playwright replay. It creates disposable accounts through the UI.
- `reproduction.log`: output from the successful fresh replay.
- `*.png`: screenshots from that replay.
- `build.log`: successful build output.
- `smoke.log`, `smoke-repeat.log`, `features.log`, `pages.log`, `offline.log`: existing browser suite output.

The baseline logs contain temporary local login links for disposable accounts. Their servers and account state no longer exist.

## Replay

From the repository root:

```sh
dotnet build Yap
npm install --prefix tests/browser
cd tests/browser
npx playwright install chromium
cd ../..
node reviews/2026-10-15-user-testing-evidence/reproduce.mjs --port=5302
```

The script accepts `--dll` and `--port` arguments. Set `YAP_REVIEW_CHROMIUM` to use an existing Chromium executable. Set `TMPDIR` to choose temporary storage on Linux.

The script copies the web root and JSON configuration into a fresh temporary content root. Persistence and the welcome page are disabled. Runtime data is removed after the server stops. Use a temporary filesystem with enough free space for the web root and browser profile.

The replay overwrites its `results.json` and screenshots. Exit code zero means the five reported defects reproduced and the navigation control passed. It does not mean the application is defect-free.

## Harness adjustments

The existing suites ran from a disposable copy of the exact commit. Their launch options selected the installed Chromium executable. Their server helper copied the web root instead of linking it, so uploads stayed isolated. Screenshot directories and ports were separated between suites. No product code was changed.

The first focused replay lost keyboard focus during the GIF tab's asynchronous card refresh. A later attempt to reopen the still-open picker then timed out. The final replay waits for that refresh before focusing the card. Its results independently confirm the earlier interactive reproduction. This harness adjustment changes no application behavior.

The smoke suite's step 17 also inspects page state too early. Both baseline logs preserve that failure. The focused navigation control waits for the page to render and passes.
