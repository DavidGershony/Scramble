# U2 — Verify unsigned rumor IDs on receive (log-only first)

**Type:** READY · **Size:** S · **Depends-on:** none

## Goal

Match Rust MDK PR #287 ("Verify unsigned application-message rumor IDs"): when we
decrypt an inner application-message rumor, recompute its canonical NIP-01 event
id and compare it to the `id` field the sender put in the rumor. A mismatch means
the sender lied about the id.

**Important safety constraint:** in THIS unit you will only **log** a mismatch
(warning), not drop the message. Dropping is a separate follow-up once we've
confirmed via logs/interop that our recomputation exactly matches real senders
(including Whitenoise). This avoids dropping legitimate messages if our canonical
serialization differs by a byte.

## Background (all you need)

- File: `src/Scramble.Core/Services/ManagedMlsService.cs`.
- In `DecryptMessageAsync`, after an application message is decrypted, the inner
  rumor JSON is parsed (look for `EnsureInnerRumorSenderMatches(doc.RootElement,
  senderHex, _logger)` — the id check goes right next to it).
- There is already a canonical id helper in the same file:
  `ComputeRumorEventId(string pubkeyHex, long createdAt, int kind,
  List<List<string>> tags, string content)`, which delegates to
  `NostrService.SerializeForEventId(...)`. **Reuse it** — do not reimplement
  hashing.

## Files you may touch

- `src/Scramble.Core/Services/ManagedMlsService.cs` (add a helper + one call)
- `tests/Scramble.Core.Tests/Mip03InteropTests.cs` (add unit tests)

Touch nothing else.

## Steps (test-first)

1. Read `DecryptMessageAsync` around the `EnsureInnerRumorSenderMatches` call and
   read `ComputeRumorEventId`. Note exactly how `tags`, `created_at`, `kind`,
   `content`, `pubkey`, `id` are (or can be) read from the rumor `JsonElement`.

2. Add an `internal static` helper next to `EnsureInnerRumorSenderMatches`:

   ```csharp
   /// <summary>
   /// MDK #287: recompute the canonical NIP-01 rumor id and compare it to the
   /// id the sender embedded. Returns true if they match OR the rumor lacks the
   /// fields needed to recompute (nothing to verify). Returns false only on a
   /// definite mismatch. Pure/testable; does not throw.
   /// </summary>
   internal static bool InnerRumorIdMatches(System.Text.Json.JsonElement rumorRoot)
   {
       if (!rumorRoot.TryGetProperty("id", out var idProp)) return true;
       var claimedId = idProp.GetString();
       if (string.IsNullOrEmpty(claimedId)) return true;
       // Extract the fields the canonical id is computed over. If any required
       // field is missing/unparseable, skip (return true) — there is nothing to
       // verify against.
       // ... (use the same extraction the method already does for kind/content/
       //      created_at/pubkey/tags; build List<List<string>> tags) ...
       var recomputed = ComputeRumorEventId(pubkey, createdAt, kind, tags, content);
       return string.Equals(recomputed, claimedId, StringComparison.OrdinalIgnoreCase);
   }
   ```

   Fill in the extraction using the exact JSON shape you observed in step 1.
   Parse `tags` as `List<List<string>>` (array of arrays of strings). Follow the
   existing parsing conventions in the method.

3. Call it right after the sender check in `DecryptMessageAsync`, **log-only**:

   ```csharp
   if (!InnerRumorIdMatches(doc.RootElement))
   {
       _logger.LogWarning(
           "DecryptMessage: MDK#287 rumor-id mismatch (recomputed id != claimed id) " +
           "from MLS sender {Sender}. NOT dropping yet (log-only).", senderHex);
   }
   ```

4. Add unit tests to `Mip03InteropTests.cs` (new test class
   `Mip03RumorIdVerificationTests`) using `System.Text.Json.JsonDocument`:
   - **Matching id:** build a rumor JSON whose `id` equals
     `ManagedMlsService.ComputeRumorEventId(...)` of its own fields → assert
     `InnerRumorIdMatches` returns `true`.
   - **Mismatched id:** same rumor but with a wrong `id` → assert returns
     `false`.
   - **Absent id:** rumor with no `id` field → assert returns `true` (nothing to
     verify).

   `ComputeRumorEventId` is `private`; if you cannot call it from the test,
   instead construct the expected id by round-tripping through your helper: build
   the rumor once, read back the `id` your production code would compute. If that
   is awkward, make `ComputeRumorEventId` `internal` (it is in a class already
   visible to tests) — that is an allowed change for this unit.

## Verify (exact commands)

```bash
# from repo root
dotnet build src/Scramble.Core/Scramble.Core.csproj -c Debug -f net10.0
dotnet test tests/Scramble.Core.Tests/Scramble.Core.Tests.csproj -c Debug \
  --filter "FullyQualifiedName~Mip03RumorIdVerificationTests"
```

## Acceptance criteria

- New unit tests pass (match / mismatch / absent).
- `Scramble.Core` builds clean on `net10.0` (0 errors).
- Production wiring **logs only** on mismatch — it must NOT drop or throw.

## Scope guards

- Do not drop/throw on mismatch in this unit (log-only by design).
- Do not change `ComputeRumorEventId`'s logic (making it `internal` is fine).
- Do not touch the sender-verification code beyond placing the new call next to
  it.

## Report back

Paste the final helper (with your filled-in extraction), confirm the three test
results, and note whether you had to widen `ComputeRumorEventId` visibility. Flag
for the orchestrator: "ready to flip log-only → drop after interop confirms no
false mismatches." Do not commit.
