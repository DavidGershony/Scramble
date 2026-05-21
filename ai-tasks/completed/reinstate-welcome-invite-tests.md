# Reinstate welcome/invite tests skipped after CanProcessWelcomeAsync gate

## Background

Commit `706fd66` ("Add skipped-invite notification for no-key Welcome events")
added a filter in `MessageService.HandleWelcomeEventAsync`: before saving a
`PendingInvite`, the service now calls `_mlsService.CanProcessWelcomeAsync(welcomeData)`
and silently dismisses the welcome (incrementing a "skipped invite" counter)
if the current device has no matching key material.

Behavior per backend:
- **Managed** (`ManagedMlsService.CanProcessWelcomeAsync`): returns `false` when
  `_storedKeyPackages.Count == 0` or when `PreviewWelcomeAsync` fails for every
  stored KeyPackage.
- **Rust** (`MlsService.CanProcessWelcomeAsync`): pass-through — always returns
  `true`. (It cannot pre-check; the real attempt happens on accept.)

## What broke

Five tests push a random 64-byte buffer as a kind-444 welcome and assert a
`PendingInvite` appears. Under the managed backend they now fail (random bytes
can't be previewed against any KeyPackage → `CanProcessWelcomeAsync` → `false`
→ welcome is dismissed, no invite surfaces). Under rust they still "pass" only
because the rust path is pass-through — which is an accidental property, not
intent.

Skipped tests (with `[Skip = "Obsolete: since commit 706fd66..."]`):
- `tests/OpenChat.UI.Tests/HeadlessRealMlsIntegrationTests.cs`
  - `PendingInvite_ArrivesViaObservable_AppearsInChatList`
  - `ChatListView_RendersPendingInvites`
- `tests/OpenChat.UI.Tests/HeadlessGroupLifecycleTests.cs`
  - `DeclineInvite_RemovesFromPendingList`
  - `RescanInvites_FindsMissedWelcomes`
- `tests/OpenChat.Core.Tests/EndToEndChatIntegrationTests.cs`
  - `WelcomeEvent_ReceivedTwice_OnlyCreatesOneInvite`

## Fix strategy

Each test needs to deliver a **real** MLS welcome that the receiving user's
stored KeyPackage can process. Required steps for each test:

1. Create two users A (sender) and B (recipient), each with their own
   `StorageService` + `IMlsService`.
2. Have B generate a KeyPackage: `await mlsB.GenerateKeyPackageAsync()` — this
   stores the private key material in B's storage so `CanProcessWelcomeAsync`
   later returns `true`.
3. Have A create a group: `await mlsA.CreateGroupAsync(...)`.
4. Have A add B using B's KeyPackage bytes:
   `await mlsA.AddMemberAsync(groupId, bKeyPackageBytes)` — this returns the
   welcome blob.
5. Wrap the welcome blob into a `NostrEventReceived { Kind = 444, Content =
   Convert.ToBase64String(welcomeBlob), Tags = [["p", bPubKey], ["h",
   groupIdHex], ["e", keyPackageEventId]] }`.
6. Push the event into B's mocked `NostrService.Events` subject — B's
   `MessageService` will now accept the welcome and raise a `PendingInvite`.

## Helper to add

Suggest extracting a `TestHelpers/RealWelcomeFactory.cs` that returns a
`(NostrEventReceived welcomeEvent, byte[] rawWelcome)` tuple given two
`RealTestContext` instances. Most tests then become a two-liner:

```csharp
var welcomeEvent = await RealWelcomeFactory.CreateAsync(senderCtx, recipientCtx, groupId: "deadbeef");
recipientCtx.EventsSubject.OnNext(welcomeEvent);
```

## Out of scope

- Rewriting `CanProcessWelcomeAsync` to differentiate "malformed" from
  "no matching key" — current behavior is intentional.
- Changing the skipped-invite counter flow — verified by `SkippedInviteTests`
  in Core.Tests.

## Acceptance

- Remove `Skip = ...` from all five tests.
- `dotnet test OpenChat.Desktop.slnf --filter "Category!=Relay"` passes with
  **0 skipped** in that set (other skips unrelated to this task may remain).
