# P11 — cutover plan

Drafted 2026-09-15, before any code moves; **decided the same day** (§6).
`CLAUDE.md` calls P11 "the only phase with real I4/I5 exposure", and the
sequencing below exists so that exposure is decided in advance rather than
discovered.

**The three decisions, made:** existing groups are **abandoned**, not migrated;
**Android leads**; there are **no existing users**, which is what makes the first
answer cheap.

Provenance: the plan's P11 row in `scramble-marmot-phased-plan-2026-08.md` §3,
and `remaining-work-2026-09.md` §3.

---

## 1. What the reconnaissance actually found

Better than the row suggests, in one way, and worse in another.

**The seam already exists.** `Scramble.Presentation` binds to interfaces —
`IMlsService`, `IMessageService`, `INostrService`, `IStorageService` — never to
concrete services. So the cutover is an implementation swap behind a contract
that already holds, not a rewrite through the UI.

**One leak, and it is trivial.** The only `MarmotCs` reference anywhere in
`Scramble.Presentation` is `SettingsViewModel.cs:141`, a version string for the
settings screen. Nothing else crosses. The Dark Matter cutover rule — no Marmot
types in `Scramble.Presentation` — is already satisfied by the old engine and
must stay satisfied by the new one.

**The size is in the implementations, not the contracts.**

| | Lines | Members | Fix-density |
|---|---|---|---|
| `NostrService.cs` | 3,664 | `INostrService`: 51 | 0.52 |
| `MessageService.cs` | 3,599 | `IMessageService`: 51 | 0.34 |
| `ManagedMlsService.cs` | 1,949 | `IMlsService`: 20 | 0.44 |
| `ExternalSignerService.cs` | 1,605 | — | **0.59** |
| `EncryptedSqliteStorageProvider.cs` | 210 | — | — |

11,027 lines, four of the five on the repo's own high-risk table.

**`IMlsService` is the real cutover.** Its twenty members map almost
one-for-one onto the session layer — `CreateGroupAsync`, `ProcessWelcomeAsync`,
`EncryptMessageAsync`, `DecryptMessageAsync`, `ProcessCommitAsync`,
`StageAddMemberAsync`/`MergeStagedAsync`/`ClearStagedAsync`, `UpdateKeysAsync`,
`ExportGroupStateAsync`. `INostrService` is mostly transport — relays,
subscriptions, NIP-46 — and should barely move. Do not treat the three as one
job; they are not.

---

## 2. The risk that was novel, and is now gone

**Decided: abandon.** No migration is written. `EncryptedSqliteStorageProvider`
wraps `MarmotCs.Storage.Sqlite` and holds MLS group state — ratchet trees,
epochs, message records — and the new engine simply will not read it. Existing
groups stop working.

That was the only step with no rollback, and **there are no existing users**, so
it costs nothing. Dual-read was considered and rejected on purpose: it
contradicts the no-compatibility-shim scope decision of 2026-09-13.

**What is emphatically not abandoned: account identity.** Checked rather than
assumed, because the two are easy to conflate:

| | Where | Survives? |
|---|---|---|
| nsec / npub, contacts, relay lists, signer pairing | `StorageService` → the `User` record (`PrivateKeyHex`, `SignerLocalPrivateKeyHex`) — **zero `MarmotCs` references** | **yes, untouched** |
| MLS group state, membership, message history | `EncryptedSqliteStorageProvider` → `MarmotCs.Storage.Sqlite` | no — this is what is abandoned |
| Encryption at rest | `ISecureStorage` (DPAPI, Android Keystore, …) | yes, engine-agnostic |

So after the cutover the account is the same account, with no groups. **Dogfood
and interop devices are covered by this too** — a second identity used for
interop testing keeps its keys and loses its groups.

---

## 3. Sequence

Each step is separately verifiable and separately revertible. **Step 1 changes
no behaviour; step 2 is where the app starts using the new engine.**

1. ~~Build the fan-in~~ **done** (`dec63e2`). `InboundFanIn` resolves an
   envelope to a group and hands it to that group's session. Registration was
   the hidden half — `IRoutingIndexStorage` had no caller on *either* side, so
   the index was permanently empty.

   **Session ownership is settled** (`5c4a284`), before step 2 rather than
   during it. The cache moved onto the host, so the receive path, the send path
   and a service layer share one owner instead of each minting their own.
   `MarmotSessionHost.SessionForAsync` is the door; `OpenAsync` stays uncached
   and says plainly that it bypasses ownership. **Step 2 must use
   `SessionForAsync`.**

   **It also unblocks a real interop test.** Every test to date, interop
   included, receives by handing bytes to a session it chose itself — stepping
   over the one part a real client cannot do. A `DarkMatterInterop` test can now
   take a raw kind-445 off the relay not knowing whose it is. Worth running
   before step 2.
2. **Port `IMlsService` onto the session layer.** Splits in two.

   **2a. The unwired adapter — done** (`62690af`). `DarkMatterMlsService`
   exists and nothing constructs it. Eleven members map cleanly, thirteen are
   adapted, **seven refuse with `NotSupportedException`** rather than invent
   semantics.

   The bridge worth knowing: staging hands `CommitAsync` a transport that keeps
   the envelope and answers `Indeterminate`, which is the true statement — the
   bytes have gone to a caller who has not published them. The engine's response
   to `Indeterminate` *is* what the old contract means by "staged", so the
   decision procedure is unchanged and only its timing moves.

   **2b. Flip the registration — not started.** This is the pivot; I5's freeze
   starts here.

   **Five things to settle before 2b**, from the adapter's own report and then
   re-checked against the call sites on 2026-09-16 — which changed four of them.
   Originally six; 4 and 5 turned out to be one problem.

   1. **`SetNostrEventSigner` throws and every head calls it** — smaller than it
      looked, with a separate problem underneath it. Revised 2026-09-16; the
      earlier entry said an adapter "needs a kind-450-shaped signing call on
      `IExternalSigner`". **It does not. No new method is required.**

      There are two call sites, not "every head":
      `MainViewModel.cs:469` (local key) and `MainViewModel.cs:639`
      (external signer, via `WireExternalSigner`).

      **The local one needs nothing.** It fires only when
      `CurrentUser.PrivateKeyHex` is non-empty, and in that case `InitializeAsync`
      has already built a `LocalAccountProofSigner` from the same key. The call is
      redundant, so it is deleted, not adapted.

      **The external one is adaptable as-is.** The refusal's reasoning — a signer
      "picks its own `created_at`" — is true of `INostrEventSigner`, whose
      `SignEventAsync(kind, content, tags, pubkey)` carries no `created_at` and so
      lets `ExternalNostrEventSigner` stamp `DateTime.UtcNow` itself
      (`INostrEventSigner.cs:140`). It is **not** true of `IExternalSigner`
      underneath, which takes an `UnsignedNostrEvent` whose `CreatedAt` we supply
      and which `ExternalSignerService` puts on the wire verbatim
      (`ExternalSignerService.cs:380`).

      So the adapter is: map our `NostrEventTemplate` to an `UnsignedNostrEvent`,
      call `SignEventAsync`, parse `sig` out of the returned event, return the 64
      bytes. **It needs no trust in the remote signer's fidelity** —
      `AccountIdentityProofSigning.CreateAsync` verifies the signature against our
      template before trusting it, so a signer that rewrote `created_at` or the
      tags produces a clean verification failure rather than a bad proof. That is
      the same guard `LocalAccountProofSigner` already leans on for a mismatched
      keypair.

      **The problem underneath: the NIP-46 permission grant is missing the kinds
      we need.** `ExternalSignerService.GenerateConnectionUri` requests

      ```
      sign_event:443, sign_event:444, sign_event:445, sign_event:1059
      ```

      (`ExternalSignerService.cs:404`). **Neither `450` nor `30443` is in it.**
      450 is the account-identity proof this item exists to sign. 30443 is the
      KeyPackage — and `NostrService.PublishEventAsync` routes it through
      `_externalSigner` whenever there is no local key
      (`NostrService.cs:3324`), so this is a **pre-existing** gap that the
      cutover merely makes load-bearing: the engine's join path is fail-closed on
      a published KeyPackage, so a user who cannot publish one cannot be invited.

      *Unverified, and it needs a device to settle:* NIP-46 implementations differ
      on ungranted kinds — Amber may prompt per signature rather than refuse. So
      the symptom is somewhere between "an extra approval dialog every time" and
      "KeyPackage publication fails". Either way the perms string is wrong and the
      fix is two entries. It is a connection-time string, so it takes effect on
      reconnect — which costs nothing here, since there are no existing users.

      **Test this against a real signer app before the flip**, not against a
      double. A mock will agree with whatever we assume, and what is in question
      is precisely what the other implementation does.
   2. **`StageUpdateAdminPubkeysAsync` throws**, so admin management is
      unavailable until an AppDataUpdate slice exists. `MessageService.UpdateAdminPubkeysAsync`
      fails at runtime.
   3. **`CommitData` now means a finished kind-445 event, not MIP-03
      ciphertext** — and this **does not break loudly**. Corrected 2026-09-16;
      the earlier entry here said "`MessageService` must stop calling
      `EncryptCommitAsync`" and called it the loud one. Both halves were wrong.

      `NostrService.PublishCommitAsync` base64-encodes whatever bytes it is
      handed, wraps them in a *fresh* kind-445, and attaches the
      `["encoding","base64"]` tag that §3 records current peers as rejecting
      before any MLS processing. Hand it a commit that is already a signed
      kind-445 and you publish base64-of-an-event inside an event. **No
      exception is thrown at any point.** It is refused by the peer, silently,
      on a tag — the same class of failure as the shipping engine's, arrived at
      from the opposite direction.

      **Six call sites, none of which throw:**

      | Site | Path |
      |---|---|
      | `MessageService.cs:826` | direct to `PublishCommitAsync` |
      | `MessageService.cs:1272` | direct |
      | `MessageService.cs:1411` | direct |
      | `MessageService.cs:1488` | `StageRemoveMemberAsync` bytes → `PublishGroupMessageAsync` |
      | `ChatViewModel.cs:918` | `catch (NotSupportedException)` → falls through to the double-wrap |
      | `ChatListViewModel.cs:1268` | same catch, same fall-through |

      **The two catch blocks are the trap.** They were written as a fallback for
      the Rust `MlsService`, which also refuses `EncryptCommitAsync`. They now
      convert the adapter's *deliberate* refusal into a wire-corrupting publish.
      A refusal that a caller already catches is not a guard; it is a comment.

      **So this is not a step-3 follow-up.** The earlier entry deferred it on the
      grounds that it would be noticed. It would not be. It has to land *with*
      the flip.

      **Fix, in two parts.** First, with 2b: a guard in `PublishCommitAsync` that
      refuses bytes which already parse as a signed Nostr event, plus the six
      call sites. One file for the guard, which catches every future site too,
      and it fails at the publish rather than at the peer. Note `NostrService` is
      a high-risk file (0.52) and under I2 — integration coverage lands with it.
      Second, at step 3: `CommitData` and `EncryptCommitAsync` leave
      `IMlsService` altogether when `marmot-cs` goes, so the ambiguous `byte[]`
      stops existing rather than being guarded.
   4. **`ProcessWelcomeAsync` lost its fail-closed KeyPackage binding, and
      nothing can tell the service a KeyPackage's published event id.** One
      problem, not two — and both halves of the data already exist. Revised
      2026-09-16; items 4 and 5 were recorded separately, and 5 read as though
      the id were unavailable. It is available on both sides; only the contract
      has no door for it.

      **The engine was built for this.** `IKeyPackageStorage` has
      `GetKeyPackageByEventAsync(eventIdHex)`, whose own summary reads: *"The
      join path's entry point: a Welcome carries the event id, and the private
      material is what has to be found from it."* The adapter's
      shuffle-and-try-every-candidate loop is a fallback for an id that never
      arrives, not a design.

      **Publish side — the caller already holds the id.** Both call sites
      generate and then publish, capturing the event id and doing nothing with
      it: `MessageService.cs:2677-2678` and `SettingsViewModel.cs:1221-1226`.
      `IKeyPackageStorage.MarkPublishedAsync(keyPackageRefHex, eventIdHex)` is
      implemented (`SqliteMarmotStorageProvider.KeyPackages.cs:104`) and already
      has a working caller in `KeyPackagePublisher.cs:240`. What is missing is a
      member on `IMlsService` to carry the id back in — one method.

      *Open detail:* the key is `keyPackageRefHex`, and the `KeyPackage` model
      returned to the caller does not obviously expose it. Either surface it on
      the model or have the new member take the `KeyPackage` itself. Decide when
      writing it; it does not change the shape.

      **Consume side — the id is already parsed and persisted.**
      `NostrService.cs:937` puts the Welcome's KeyPackage `e` tag on
      `MarmotWelcomeEvent.KeyPackageEventId`; it is stored on `PendingInvite`
      (`StorageService.cs:178`) and read back at `MessageService.cs:2638-2640`.
      So the id is in hand at the `ProcessWelcomeAsync` call site.
      `ProcessWelcomeAsync(welcomeData, wrapperEventId)` simply does not take it
      — note `wrapperEventId` is the kind-1059 wrapper, a different id, so this
      is an added parameter rather than a repurposed one.

      **Why it is worth doing rather than living with the loop.** HPKE
      decryption still decides correctness — success is proof of possession — so
      the loop is not *unsafe*. What it loses is the fail-closed refusal of a
      Welcome naming a KeyPackage we never published, and it costs a trial
      decryption per stored candidate. The randomised order
      (`Random.Shared.Shuffle`) makes that cost non-deterministic as well.

      **Mutation to write against it:** make the binding accept a Welcome whose
      `e` tag names a KeyPackage this device never published, and confirm a test
      fails. A test that only checks the happy path passes identically with the
      shuffle-and-try loop still in place, which is exactly the shape §3 keeps
      recording.
   5. **`IMlsService` stays synchronous. Decided 2026-09-16 — do not make it
      async at step 3.**

      The open question was whether the seven `Blocking<T>` bridges are a
      deadlock or freeze hazard on the UI thread. They are not, and the reason is
      structural rather than lucky:

      - **Nothing under `_gate` reaches a network.** The host is built with
        `CallerPublishes.Instance` and `messages: null`
        (`DarkMatterMlsService.cs:163`). The commit relay answers
        `Indeterminate` without awaiting anything, and the send path is not used
        at all — `EncryptMessageAsync` calls `GroupMessages.Send`, which builds
        an envelope and puts nothing on a wire. So every gate-held region is
        storage plus MLS compute, both bounded and local. A UI-thread caller
        waits behind another caller's *work*, never behind a relay timeout.
      - **All seven bodies are storage reads** — `ListKeyPackagesAsync`,
        `GetStagedCommitAsync`, `RequireSessionAsync`. Microsoft.Data.Sqlite's
        async surface completes synchronously, so `GetAwaiter().GetResult()`
        posts no continuation to a `SynchronizationContext` and cannot deadlock.
      - The one long pass in the adapter — the hydrating `ReplayAsync` — runs
        **once at construction**, before anything is opened. It is not reachable
        from a `Blocking` call.

      Against that, going async is a signature change to a file both UI heads
      bind to: an I4 flag-day shape, landing inside the I5 freeze that 2b itself
      starts. And step 3 already shrinks this contract for free — `CommitData`
      and `EncryptCommitAsync` leave it when `marmot-cs` goes (see item 3).
      Removing the wrong members beats making the wrong contract async.

      **What reverses this decision, precisely:** giving the adapter a real
      `IMessageRelay` — that is, routing sends through `MarmotSession.SendAsync`
      instead of `GroupMessages.Send`, which is the natural way to recover the
      durable send queue. That puts a relay round trip under `_gate`, and every
      `Blocking` call becomes an ANR on Android. **If that change is ever made,
      the async conversion has to land with it, not after it.** Whoever picks up
      the durable send queue should read this item first.

   **Three things the adapter needs that the old service did not:** a
   `SemaphoreSlim` around every entry point, because the engine is explicitly
   one-loop and `IMlsService` is called from the UI thread and the subscription
   loop; a re-fetch through `SessionForAsync` after `AdoptAsync`, which returns
   an *uncached* session and so a second owner; and persistence of the sender
   ratchet before returning envelope bytes.
3. **`IMessageService`**, which sits on `IMlsService` and should mostly follow.
   *Size: S–M.*
4. **`INostrService`** — audit rather than port. Most of it is transport that
   does not care which engine is underneath. *Size: S, pending the audit.*
5. **Delete `marmot-cs`** and `Scramble.Core`'s duplicate codecs — the
   duplication the standalone rule created deliberately, which was always to go
   at cutover. Includes the one `MarmotCs` reference left in
   `Scramble.Presentation`: the version string at `SettingsViewModel.cs:141`.
   *Size: S.*

`ExternalSignerService` is **not** on this list. It is the highest fix-density
file in the repo at 0.59 and it is a signer, not an engine — it should be
touched only if a step above forces it, and then in its own commit.

---

## 4. The invariants, decided now

**I4 — no flag-day rewrites.** Every step above is one subsystem. The step most
likely to breach it is 2, because porting twenty members tempts a single commit.
Split it: the no-op adapter first, verified by the full integration suite, then
the behaviour. If a step genuinely cannot be split, it takes a
`Landing-Discipline-Exempt` trailer naming why — auditable, not silent.

**I5 — pivot freeze. Android leads, decided.** This is a UI-head migration in
everything but name, so I5 binds. **Step 2 splits, and only its second half
starts the clock:** an unwired implementation changes no behaviour and is not a
pivot, so the freeze begins when the DI registration flips, not when the adapter
lands. From that moment,
`src/Scramble.UI` + `src/Scramble.Desktop` are bugfix-only** until the Android
head has an equivalent smoke test green in CI plus one week of stabilisation.
Anything that must land on desktop during the freeze takes a `Pivot-Exempt:`
trailer, so the exemptions stay auditable by `git log --grep`.

ANALYSIS.md records the 2026-05-11 pivot landing without this discipline as the
worst two-week stretch in the repo's history, at a **4.5×** fix:feature ratio,
with the legacy head still taking features throughout. That is the exact shape
this freeze exists to prevent.

**I2** applies throughout — every step touches engine or service paths, so the
integration suite is a merge gate, not an afterthought.

---

## 5. Exit criteria, restated concretely

The plan's row says "Desktop + Android smoke tests green; existing chats still
open; a full-stack E2E passes on both heads." **"Existing chats still open" is
struck** — abandon makes it false by design, and leaving it would be a criterion
nothing can satisfy. The rest, made testable:

- **The account survives.** Install over an existing build: same npub, contacts
  and relay list intact, remote signer still paired. Groups are gone, and that
  is the expected result rather than a failure.
- **Android sends and receives against a live `wn-agent` peer.** The
  `DarkMatterInterop` suite already proves the *engine* does; this proves the
  *app* does.
- **Desktop the same**, after the freeze lifts.
- `marmot-cs` is gone from the solution, nothing in `Scramble.Presentation`
  names a Marmot type, and the drift check stays clean.

**Two things found in the old engine while porting, both resolved by step 5 but
worth knowing until then.** `ManagedMlsService.EncryptCommitAsync` emits an
`["encoding","base64"]` tag on its kind-445, and §5's trap table records that
current peers reject a kind-445 carrying any tag beyond `h` and `expiration`
*before any MLS processing* — so the old engine's commits are already unreadable
by a current peer. And commits are wrapped by two different code paths
(`EncryptCommitAsync` for adds, `NostrService.PublishGroupMessageAsync` for
removes and admin updates), i.e. two implementations of the MIP-03 wrap living
in `Scramble.Core`.

---

## 6. Decisions (2026-09-15)

1. **Abandon, not migrate** (§2). No migration code. There are no existing
   users, so the only irreversible step in the phase is removed outright.
2. **Android leads** (§4). Desktop goes bugfix-only under I5 from step 2.
3. **Staged rollout: not required.** There is no beta channel today —
   `publish.yml` triggers on a `v*` tag and builds release artifacts; no Play
   Store track, no fastlane. With abandon chosen there is no data-corruption
   risk to stage, and with no users there is no blast radius. A Play internal
   track remains cheap if it is ever wanted to exercise the install path itself,
   but nothing in this plan depends on it.
