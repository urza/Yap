# Passkeys: read before the merge into main

Written 2026-10-18 on branch `worktree-passkeys`, the day the feature was built. The code is complete and verified on
the branch (55 tests, a headless-browser walk with a virtual authenticator). The merge was deferred on purpose. This
page holds the dependency and maintainer check of the one new library, and the steps for the merge and the first deploy.

## The new dependency

Yap takes on one NuGet package: `Fido2` 4.3.1 (Fido2NetLib), MIT. It does the WebAuthn cryptography behind
`Services/Accounts/PasskeyService`. Its dependencies on net10.0, from the package's own nuspec:

| Package | Owner | Why it is there |
| --- | --- | --- |
| Fido2.Models 4.3.1, then Microsoft.Bcl.Memory 9.0.14 | the library, Microsoft | the request and response shapes |
| System.Formats.Cbor 9.0.0 | Microsoft | parses the authenticator data |
| Microsoft.Extensions.Http 9.0.0 | Microsoft | the FIDO metadata service client, which Yap does not use (no `IMetadataService` is passed) |
| Microsoft.IdentityModel.JsonWebTokens 8.2.0 | Microsoft | the metadata service's signed blob, same, unused |
| NSec.Cryptography 25.4.0, then libsodium 1.0.20.1 (native) | Klaus Hartke | Ed25519 keys only. Platform passkeys (Apple, Google, Microsoft) use P-256, which .NET handles itself |

So: three Microsoft packages, and one third-party package that carries a native library. The native library is the one
thing to watch in the Docker image. The libsodium package ships binaries for linux-x64, linux-arm64 and the musl
variants, so the image should need nothing.

## Who maintains it

- Repository: [passwordless-lib/fido2-net-lib](https://github.com/passwordless-lib/fido2-net-lib), a member project
  of the .NET Foundation.
- Created and maintained by Anders Åberg. He founded Passwordless.dev, which Bitwarden bought in January 2023, and he
  works on Passwordless at Bitwarden. The top contributors are Alex Seigler, Anders Åberg and Jason Nelson.
- State on 2026-10-18: last push 2026-10-16, about 1450 stars, 9 open issues, not archived. The 4.x line had releases
  through 2026 (4.0.1 in April, 4.3.1 current). 5.0 previews exist (preview.8 is the latest); do not take one.
- The alternative was our own WebAuthn verification, about 400 lines of CBOR, COSE and signature code. Rejected:
  a tested library beats own crypto code, and this is the only maintained .NET WebAuthn server library of note.

## Before the merge

1. Check NuGet for a newer `Fido2` 4.x and take it when one is out. The code uses a small surface: the `Fido2`
   constructor, `RequestNewCredential`, `MakeNewCredentialAsync`, `GetAssertionOptions`, `MakeAssertionAsync`, and
   `Fido2Configuration.RPID`, `RPName`, `Origins`. A 5.0 may rename these; stay on 4.x.
2. Expect a conflict in `docs/spec/sessions.md`: main's copy changed after the branch was cut. The branch adds the
   section "Passkeys" (AU-27 to AU-30) before "Invites", and touches AU-08, AU-20, AU-22, the States table and the
   Wording table. Rebuild the HTML after the merge (`python3 docs/spec/tools/build.py`, `python3 docs/architecture/tools/build.py`).
3. Run `dotnet test` after the merge. `Yap.Tests/PasskeyTests.cs` has the software authenticator; it needs no network.

## At the first deploy with passkeys

1. The migration `20261018133946_Passkeys` creates one table, `Passkeys`, with a cascade from `Users`. No rebuild of
   existing tables; nothing to back up beyond the usual.
2. No new configuration. The relying party is the host of each request, and both `https://{host}` and
   `http://{host}` are accepted as the origin, so `TrustedProxies` and `PublicOrigin` do not affect passkeys.
3. On the test instance, add a passkey in Settings and sign in with it once on a second browser. That confirms the
   image loads the library on its runtime. A failure to load libsodium would show only for an Ed25519 key, which
   platform passkeys never present, so a working P-256 round trip is the real check.
4. Passkeys exist until a restart when persistence is off, like accounts (AU-06). With persistence on they are rows.
