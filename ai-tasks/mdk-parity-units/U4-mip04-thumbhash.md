# U4 — MIP-04 thumbhash emit + parse

**Type:** READY (read-first) · **Size:** S · **Depends-on:** none

## Goal

Match Rust MDK PR #244: emit a `thumbhash` value in the `imeta` tag of outbound
encrypted-media (kind:445) messages **alongside** the existing `blurhash`, and on
inbound parse **both** `blurhash` and `thumbhash` (thumbhash preferred, blurhash
kept for backward compatibility).

## Background (all you need)

- Outbound media `imeta` tags are built in
  `src/Scramble.Core/Services/MessageService.cs` (media send path — search for
  `imeta` and `blurhash`).
- Inbound `imeta` parsing happens in
  `src/Scramble.Core/Services/ManagedMlsService.cs` (`DecryptMessageAsync` parses
  `imeta` entries — search for `blurhash` / `imeta`).
- `src/Scramble.Native` already has `fast_thumbhash` bindings — check how other
  native calls are invoked from C# before adding a new one; **reuse the existing
  interop pattern**, do not hand-roll a new FFI mechanism.

## Files you may touch

- `src/Scramble.Core/Services/MessageService.cs` (emit `thumbhash`)
- `src/Scramble.Core/Services/ManagedMlsService.cs` (parse `thumbhash`)
- The Scramble.Native binding surface **only if** a thumbhash-compute entry point
  is not already exposed to C# (prefer reusing an existing one)
- `tests/Scramble.Core.Tests/MediaMessageImetaTests.cs` (add tests)

## Steps (read-first)

1. **Read** the outbound `imeta` builder and the inbound `imeta` parser and the
   existing `MediaMessageImetaTests` to learn the exact tag shape and test
   conventions. Confirm how (and whether) `blurhash` is currently computed and
   where a `thumbhash` could be produced from the same image bytes.
2. Outbound: when building `imeta`, add a `thumbhash <value>` entry next to
   `blurhash` when a thumbhash is available. Do not remove `blurhash`.
3. Inbound: parse a `thumbhash` entry into the message model (add a field if
   needed, mirroring the existing `blurhash` field). Accept messages that carry
   either or both; prefer `thumbhash` when both are present.
4. Add tests: (a) an outbound media imeta contains both `blurhash` and
   `thumbhash`; (b) inbound parsing reads `thumbhash`; (c) inbound with only
   `blurhash` still parses (back-compat).

## Verify (exact commands)

```bash
dotnet build src/Scramble.Core/Scramble.Core.csproj -c Debug -f net10.0
dotnet test tests/Scramble.Core.Tests/Scramble.Core.Tests.csproj -c Debug \
  --filter "FullyQualifiedName~MediaMessageImetaTests"
```

## Acceptance criteria

- Tests pass; `Scramble.Core` builds clean on `net10.0`.
- `blurhash` is still emitted and still parsed (no back-compat regression).

## Scope guards

- Do not remove or change `blurhash` behavior.
- Do not restructure the media pipeline; this is an additive field.
- If computing a thumbhash requires new native plumbing that isn't already
  exposed, STOP and report rather than inventing an FFI surface.

## Report back

Confirm where thumbhash is computed (existing native entry point or new), paste
the imeta before/after, and the test results. Do not commit.
