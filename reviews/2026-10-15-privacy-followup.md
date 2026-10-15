# Yap-r2: privacy and authorization follow-up

The follow-up confirms **eight additional security findings** in the current rewrite. It also confirms another consequence of F01 from the [first review](2026-10-15-review.md).

The checks used disposable local accounts and synthetic content. No production accounts or private services were tested. No application source changes were made.

Reviewed commit: `3cbdf5ed8632fb5d6f0df162be0bcdf3c2525155`. Date: 2026-10-15. Existing local deletions under `Yap.Tests/` and changes to `Yap.slnx` remain untouched.

## Findings at a glance

P1 findings should block release until fixed or explicitly constrained by verified deployment controls. P2 findings also need correction, but generally require a previously known resource identifier.

| ID | Priority | Confirmed defect | Required condition |
| --- | --- | --- | --- |
| S01 | P1 | Live edits disclose message text excluded by history restrictions | A restricted viewer has the room open when an older message is edited |
| S02 | P2 | Private GIF operations do not enforce the entry's visibility | Another signed-in person knows the GIF entry ID |
| S03 | P2 | Upload operations omit ownership checks; completion information is anonymous | The caller knows an upload ID |
| S04 | P2 | One push removal route permits removal of another account's subscription | A signed-in caller knows the subscription endpoint |
| S05 | P1 | Smart login trusts client-controlled proxy headers | Smart login is enabled, a recognized victim IP is known, and the deployment accepts those headers |
| S06 | P1 | A request can change the origin used in other users' login links | PublicOrigin is absent and a supplied Host reaches the app |
| S07 | P1 | An approved registration can be claimed by a different requester | Another requester knows the pending username and submits first after approval |
| S08 | P1 | Redirects bypass the private-network checks for remote content | An accessible public URL redirects to a service reachable by the Yap server |

## S01. Apply history visibility to live events

Sources: [ChatHubBroadcaster.cs:27](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Endpoints/ChatHubBroadcaster.cs:27), [ChatHubBroadcaster.cs:75](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Endpoints/ChatHubBroadcaster.cs:75), and [ChatService.cs:336](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Services/Chat/ChatService.cs:336).

The initial history response respects the room's history cutoff. Live broadcasts do not apply that same check to each recipient.

The test created a room with history limited to messages after the viewer joined. An administrator posted a private test string before creating the viewer's account. The viewer opened the room and received zero messages, with `restricted=true`.

The administrator then edited that older message. The viewer's SignalR connection received the complete message object, including `HIDDEN_EDITED_SECRET`.

The normal client ignores an edit for a row it has not loaded. That does not protect the text: it already reaches the browser. The receiver needs no hidden message ID to observe this event.

The same test confirms a related F01 case. An ordinary administrator reply to the older message includes its excluded text in the new message's quote. This quote behavior is an extension of the original finding, not a separate ninth finding.

**Fix:** apply a common read-authorization rule to history, reply mapping, and live event delivery. A room-wide event cannot carry restricted text to every member. Review preview events through the same rule, since they can contain information derived from older messages.

**Regression check:** create a member after the original message, then edit and reply to that message. Inspect received events as well as rendered rows.

Evidence: `results.json`, test `History cutoff: reply quote and edit broadcast`. Relevant requirement: TL-05.

## S02. Check GIF visibility before resolving an entry ID

Sources: [GifService.cs:365](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Services/Gifs/GifService.cs:365), [GifEndpoints.cs:52](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Endpoints/GifEndpoints.cs:52), and [GifEndpoints.cs:60](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Endpoints/GifEndpoints.cs:60).

Local search filters private uploads by owner or existing favorite. The send, favorite, and tag routes do not establish the same visibility before acting on an entry ID.

Alice uploaded a GIF to her personal library without sharing it. Eve's search returned no matching entries. With Alice's entry ID, Eve could nevertheless:

- Send the GIF to the lobby and obtain its file URL and uploader name.
- Replace its tags.
- Add it to her favorites and receive its metadata.
- Export a GIF package after adding it to her library.

Deleting Alice's GIF returned 403. That ownership check works, but the other operations bypass the private-library boundary.

**Qualification:** the test supplies a known entry ID. It does not demonstrate ID enumeration or discover an unknown GIF. The product already serves uploaded media by URL; this finding concerns the missing checks on private-library operations and metadata changes.

**Fix:** define who may use a GIF entry and check that rule before sending, favoriting, or editing it. Shared messages can grant access to their actual readers. An unverified favorite request must not grant itself access to an otherwise private entry.

**Regression check:** an unrelated account with a known private entry ID cannot use or modify it. A legitimate message recipient can still favorite the shared GIF.

Evidence: `results.json`, test `Private GIF: unrestricted send, tags, favorite and export`. Relevant requirements: PK-26 and PK-41.

## S03. Bind upload operations and completion information to the uploader

Sources: [TusEndpoints.cs:36](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Endpoints/TusEndpoints.cs:36) and [TusEndpoints.cs:300](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Endpoints/TusEndpoints.cs:300).

The TUS authorization handler checks whether the caller is signed in. It does not check whether the caller owns the named upload.

In the test, an anonymous HEAD request to an unfinished upload returned 401. Eve's authenticated HEAD request to Alice's upload returned 200 and exposed its original filename. Eve then deleted it with HTTP 204. Alice's subsequent request returned 404.

The completion route has a second problem. An anonymous request to `/api/tus/info/{fileId}` returned Alice's completed GIF entry ID. That read removed the result from `_completedFiles`. Alice's own next request returned 404, so the normal upload flow could no longer retrieve its result.

**Qualification:** both cases require the upload ID. No method of enumerating upload IDs was established. These checks used small test files only.

**Fix:** record the authenticated uploader when creating an upload. Enforce that owner on HEAD, PATCH, DELETE, and completion-info reads. Require authentication on the completion route. Do not let an unauthorized read consume its result.

**Regression check:** a second account and an anonymous caller cannot inspect, modify, terminate, or consume another account's upload.

Evidence: `results.json`, tests `Upload completion info is anonymous and consumed by first reader` and `Other account can inspect and terminate unfinished upload`.

## S04. Use the same ownership rule on every push removal route

Source: [PushEndpoints.cs:43](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Endpoints/PushEndpoints.cs:43). Related caller: [SignInEndpoints.cs:29](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Endpoints/SignInEndpoints.cs:29).

The DELETE device route verifies that a subscription belongs to the caller. The POST unsubscribe route removes a supplied endpoint without that check.

The test registered a synthetic subscription for Alice. Eve's DELETE through the device route returned 404. Eve's POST through the unsubscribe route returned 200 and removed Alice's subscription. Alice's device list then contained zero subscriptions.

The sign-out endpoint also passes a supplied push endpoint directly to the same unscoped removal method. That additional caller was identified by code review; the runtime reproduction used `/api/push/unsubscribe`.

**Qualification:** a subscription endpoint is a high-entropy value. The test assumes it is known. No real push provider was contacted, and no notification payload was obtained.

**Fix:** make removal account-scoped in the service interface. Apply it consistently to settings, unsubscribe, and sign-out. Keep any deliberate administrative operation separate.

Evidence: `results.json`, test `Other account can unsubscribe a push endpoint`. Relevant requirement: NT-24.

## S05. Do not use untrusted proxy headers as login credentials

Sources: [IpHelper.cs:13](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Helpers/IpHelper.cs:13) and [SignInService.cs:172](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Services/Accounts/SignInService.cs:172).

`IpHelper` accepts `CF-Connecting-IP` or the first `X-Forwarded-For` value without verifying the immediate proxy. Smart login treats the resulting value as evidence that the caller uses a recognized network.

The test registered Alice with a synthetic recognized address. An unauthenticated sign-in from a different synthetic address returned 401 and `Code required`. Supplying Alice's recognized address in the proxy header returned 200 and issued an authentication cookie without a code.

**Deployment condition:** this matters when Kestrel is reachable directly, or when a proxy forwards these headers without replacing them safely. A deployment that accepts connections only from trusted proxies and strips spoofed headers can prevent this path. That configuration was not verified for any production instance.

**Fix:** process forwarded addresses only from explicitly trusted proxies. Use the actual peer address for other requests. Do not read the raw forwarding headers independently afterward. Verify both Cloudflare and ordinary reverse-proxy deployment paths.

**Regression check:** changing a header from an untrusted peer does not change smart-login eligibility.

Evidence: `results.json`, test `Smart login trusts spoofed proxy IP headers`. Relevant requirement: AU-09.

## S06. Do not share a mutable request-derived origin between accounts

Sources: [AccessLinkService.cs:249](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Services/Accounts/AccessLinkService.cs:249), [RequestOrigin.cs:10](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Helpers/RequestOrigin.cs:10), and [BootstrapEndpoints.cs:40](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Endpoints/BootstrapEndpoints.cs:40).

When PublicOrigin is absent, a request can replace the singleton's `_publicOrigin`. Other users' login links then use that value.

The test first retrieved Alice's link with the local server origin. A request from Eve supplied the synthetic host `attacker.invalid`. Alice's next link response contained the same credential code under `https://attacker.invalid/`.

The test did not navigate to that domain. It demonstrates that the application can give a user a credential-bearing link with an origin chosen by another requester. Following or sharing such a link can disclose its code to that destination.

**Configuration conditions:** the test omitted PublicOrigin. A non-empty trusted PublicOrigin overrides this behavior. The checked-in configuration has an empty string, which takes a different code path because the service tests for null. This finding must not be described as applying identically to every configuration. Host restrictions at a reverse proxy can also prevent the tested request from reaching the app.

**Fix:** use a configured trusted public origin, or derive user-facing links from that specific request after trusted proxy and host validation. Avoid a global “last caller's origin” value. Background messages need a trusted origin as well.

Evidence: `redirect-results.json`, test `Global login-link origin poisoning`. Relevant requirement: AU-15.

## S07. Bind registration approval to the original requesting device

Sources: [SignInService.cs:76](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Services/Accounts/SignInService.cs:76) and [RegistrationGateService.cs:114](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Services/Accounts/RegistrationGateService.cs:114).

Pending and approved registrations are keyed only by username. Any requester who submits that username can consume its approval.

The original requester submitted a new username and received 202. After the administrator approved it, a different requester submitted the same username and received 200 with a signed-in account. The original requester then received 401 and `Code required`.

Smart login was disabled for this test, so the result does not depend on S05. This affects creation of an approved account, rather than access to an existing account's historical messages.

**Fix:** bind each pending registration to an unpredictable request token held by its originating device. Approval should authorize that request. A username alone must not be sufficient to claim it.

**Regression check:** after approval, only the browser holding the original request token can finish that registration.

Evidence: `results.json`, test `Approved registration can be claimed by another requester`. Relevant requirement: AU-05.

## S08. Revalidate remote-content destinations after redirects

Sources: [GifService.cs:1018](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Services/Gifs/GifService.cs:1018), [LinkPreviewService.cs:142](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Services/Chat/LinkPreviewService.cs:142), and HTTP client configuration in [Program.cs:100](/d/PROJECTS/d3mnjx/repos/urza/Yap-r2/Yap/Program.cs:100).

Both paths check the original hostname before fetching. Their HTTP clients then follow redirects without applying the same private-address check to the next destination.

Two local reproductions confirmed the consequence:

- A direct request for a loopback GIF was blocked. A redirect to the same local test service caused the server to fetch its bytes. Those bytes became readable through the application's GIF cache. The bytes were the synthetic string `PRIVATE_INTERNAL_SECRET_BYTES`.
- A link-preview redirect fetched a local test page. Its synthetic private title and description appeared in the message preview.

The GIF path accepted the client-supplied provider item even without a configured provider. The test did not need a valid provider-issued result. The cached response did not need to contain a valid image to become retrievable as bytes.

**Test method:** a loopback mock proxy supplied the initial redirect response for a public-host URL. A separate loopback HTTP service held the synthetic private data. The application performed the redirected request itself. No real internal endpoint, cloud metadata service, or private credential was queried.

**Fix:** disable automatic redirects and validate every destination before following it. Enforce the allowed address at connection time to address DNS changes as well. For provider GIFs, validate the provider and source URLs instead of trusting an arbitrary client-supplied item. An outbound network policy adds protection but does not replace these application checks.

**Regression check:** both GIF normalization and link previews refuse private-address redirect targets while normal public redirects continue to work.

Evidence: `redirect-results.json`, both tests beginning `SSRF:`.

## Controls that passed

An unrelated authenticated account could not fetch the DM history through the normal history method. It also could not send into that DM, edit or delete its message, or react to it. An ordinary account received 404 from an administrator endpoint. Deleting another user's GIF returned 403. An anonymous request to an unfinished TUS upload returned 401.

These controls show why authentication alone is insufficient. Some entry points enforce the relevant resource boundary, while secondary paths accept the same identifiers without equivalent checks.

## Recommended order

1. Correct history visibility and reply authorization together. Check outgoing events, not just rendered screens.
2. Correct remote-fetch redirects and the trust boundary for login-related headers and origins.
3. Bind approved registrations to their originating requests.
4. Centralize the ownership and visibility checks for GIFs, uploads, and notification subscriptions.

The fixes should include the negative tests described above. A successful happy-path test does not verify resource isolation.

## Evidence and limits

The [evidence directory](2026-10-15-privacy-evidence/README.md) contains the test results and method notes. Tests ran against the local ASP.NET Core app through HTTP and authenticated SignalR long polling. No source instrumentation was needed.

The build passed with the same warnings recorded in the first review. The test host used temporary data and an empty temporary web root. Optional media executables were omitted from PATH to keep startup within the container's remaining disk space. The tested GIF upload used the application's ImageSharp fallback.

The first redirect/origin attempt did not fully exercise those hypotheses because of the HTTP test client's behavior and inherited proxy settings. The controlled repeat confirmed S06 and S08. Both sets of results are retained so that the evidence is not misleading.

This is a targeted review of the current code. It does not establish which defects predate the rewrite, and it does not certify a particular production proxy configuration. The known-ID findings do not include evidence that those IDs are enumerable.
