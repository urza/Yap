# Privacy review evidence

See [the follow-up report](../2026-10-15-privacy-followup.md) for findings, conditions, code locations, and fixes.

| File | Contents |
| --- | --- |
| `results.json` | Structured results from the first targeted run, including successful negative controls |
| `results.log` | Console output from that run |
| `redirect-results.json` | Controlled repeat for login-link origins and remote-content redirects |
| `redirect-results.log` | Console output from the repeat |
| `build.log` | Successful build and compiler/package warnings |

All accounts, identifiers, login-link codes, addresses, and private-content markers are synthetic test data. Authentication cookie values are not included.

The app ran on loopback port 5121, with persistence disabled and isolated temporary storage. SignalR checks used its supported long-polling transport. The remote-content checks used a mock proxy on 5122 and a synthetic private service on 5123. These services only existed for the test.

The first run did not confirm the origin and redirect hypotheses. The HTTP client's handling of Host and the inherited proxy configuration prevented the intended test setup. The repeat used an explicit HTTP Host request and set both uppercase and lowercase proxy variables. It confirmed both cases. These initial false results are test setup outcomes, not evidence of effective application protection.

For the redirect check, the mock proxy supplied a redirect response for the original public-host URL. The application's own HTTP client followed it to the local synthetic service. The returned markers show what crossed that boundary. This does not involve any production private service.

For the GIF export check, the evidence records a successful package response after the unrelated account added the entry to its library. The test did not independently inspect the ZIP archive contents. The direct send response separately confirms the private entry's file URL and uploader disclosure.

No production messages, cookies, uploads, notification subscriptions, or credentials were used. No notification was sent to a real provider.

The `.mjs` files preserve the executed local harnesses. They use Node 22 built-ins and no browser packages. They retain Linux scratch paths and ports; adapt those paths before replay. Build the app first and create `/tmp/yap-r2-security` before running. The scripts accept `YAP_REPO` for the checkout location.
