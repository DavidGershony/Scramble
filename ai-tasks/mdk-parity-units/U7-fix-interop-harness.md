# U7 — Fix the Whitenoise interop harness (decrypt only the target event)

**Type:** READY · **Size:** M · **Depends-on:** none

## Goal

The interop diagnostic `GroupChat_3Users_2Scramble_1Whitenoise` (and its siblings)
fail on `master` — verified against base `b158675` — because after Whitenoise
sends a reply, the test **fetches every kind:445 event for the group and tries to
decrypt each one** with Alice and Bob. That set includes events the member
already consumed (its own earlier message) and events at other epochs, producing
`Generation already consumed` and `AEAD authentication failed` noise, so the
target message is never cleanly matched. This is a **harness bug, not a protocol
bug** (production decrypts each event once, in order, via subscription). Fix the
harness so its signal is trustworthy.

## Background (all you need)

- File: `tests/Scramble.Diagnostics/WhitenoiseGroupInteropTests.cs`.
- Around the "Charlie (WN) sends reply" step it does:
  `FetchRawEventsFromRelay(..., kinds=[445], limit=20)` then a
  `foreach (var ev in groupMsgEvents)` loop that tries
  `alice.MlsService.DecryptMessageAsync(...)` / `bob...` on **every** event until
  one yields `"Hello from Whitenoise!"`.
- The three interop tests are `GroupChat_3Users_2Scramble_1Whitenoise`,
  `GroupChat_4Users_2Scramble_2Whitenoise`, `GroupChat_WhitenoiseCreatesGroup_ScrambleJoins`.
- Tests are `[Trait("Category","WhitenoiseInterop")]` and **skip** when the WN
  docker isn't up (`Assert.SkipWhen(_wnClient == null, ...)`).

## Prerequisite: bring up the WN docker

```bash
# from repo root
docker compose -f docker-compose.test.yml down -v
docker compose -f docker-compose.test.yml up -d
sleep 6
docker ps   # confirm whitenoise-interop + nostr-relay are Up
```
If `whitenoise-interop` isn't running, check `docker logs whitenoise-interop`.
(A stale `wn-data` volume causes a `MissingEncryptionKey` crash — the
`down -v` above wipes it.)

## Files you may touch

- `tests/Scramble.Diagnostics/WhitenoiseGroupInteropTests.cs` only.

## Steps

1. Read the reply-decryption loop in each of the three interop tests.
2. Change the decryption logic so it does **not** blindly decrypt every fetched
   445 event. Preferred approach: identify the WN reply event specifically — e.g.
   decrypt in **relay/created_at order** and **stop at the first event that
   decrypts to the expected plaintext**, and **swallow expected non-target
   errors** (`Generation already consumed`, AEAD failures) without letting them
   fail the test. The assertion should be "the target reply was decrypted by
   Alice and Bob," not "every 445 event decrypts."
   - Do not weaken the actual interop assertion (Alice and Bob MUST decrypt the
     WN reply). Just stop treating already-consumed/other-epoch events as
     failures.
3. If the same decrypt-everything pattern exists for the forward direction, leave
   the forward direction alone unless it has the same false-failure problem.

## Verify (exact commands)

```bash
# WN docker must be up (see prerequisite)
dotnet test tests/Scramble.Diagnostics/Scramble.Diagnostics.csproj -c Debug \
  --filter "FullyQualifiedName~GroupChat_3Users_2Scramble_1Whitenoise"
dotnet test tests/Scramble.Diagnostics/Scramble.Diagnostics.csproj -c Debug \
  --filter "Category=WhitenoiseInterop"
```

## Acceptance criteria

- The three `WhitenoiseInterop` tests pass with the WN docker up (or skip when it
  is down — never hang).
- The interop assertions still require Alice and Bob to decrypt Charlie's WN
  reply — you did not delete/weaken the real check, only removed the
  decrypt-everything noise.

## Scope guards

- Test file only. Do not change any `src/` or `lib/` production code.
- Do not change what "success" means (both OC members decrypt the WN message).

## Report back

Paste the before/after of the decryption loop, and the pass/fail of both verify
commands. Note if any of the three tests still fails and why (with the exact
assertion + error). Do not commit.
