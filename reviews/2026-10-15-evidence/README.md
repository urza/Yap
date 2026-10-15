# Review evidence

See [the review](../2026-10-15-review.md) for findings, source locations, and reproduction steps.

All accounts, messages, invitation codes, and IDs in these logs are disposable test data.

| File | Contents |
| --- | --- |
| `smoke.log` | First baseline run, including the timing-sensitive navigation failure |
| `smoke-repeat.log` | Repeat baseline, 17 passes |
| `offline.log` | Offline baseline, 10 passes; expected disconnected-request errors are recorded |
| `features.log` | Feature baseline, 9 passes |
| `pages.log` | Settings/admin baseline, 14 passes |
| `review.log` | F01, F02 sign-out, F04, F05, F10, F12 |
| `review2.log` | F03, F07, F08, F09, F11 |
| `review3-repeat.log` | F02 account switch; also records a navigation hypothesis that was not confirmed |
| `persistence.log` | F06, database contents and duplicate POST results across restart |
| `server-tests-result.log` | 15 server tests passed, using temporary copies from HEAD |
| `phone-picker-after-navigation.png` | Empty phone picker after leaving and returning to chat |
| `scripts/` | Snapshots of the targeted test harnesses as executed |

The script snapshots retain the review machine's temporary paths and Chromium executable path. They are evidence, not a new project test runner. To replay them, build the app, create an isolated scratch copy of `Yap/wwwroot`, `Yap/Data`, and `tests/browser`, and adjust those paths. Install Playwright in the scratch test directory. Run each JavaScript script with its own `--port` and `--dll` arguments. The copied `lib.mjs` determines its repository root relative to `tests/browser`.

The review used ports 5097 through 5104. The persistence script used 5102 and kept its database between two server starts. Normal browser scripts removed their temporary content roots when they finished. Their web-root symlink pointed to a scratch copy, so uploads did not modify repository data.

The original browser scripts were unchanged except for selecting the available Chromium executable in temporary copies. Screenshots from the baseline tests were not regenerated in `docs/spec/img`.

The successful server-test run used `dotnet vstest` against temporary test binaries, with `TMPDIR` on `/dev/shm`. Its PATH omitted optional `yt-dlp` and ffmpeg executables to avoid exhausting temporary storage during concurrent test-host startup. No application source changes were needed.
