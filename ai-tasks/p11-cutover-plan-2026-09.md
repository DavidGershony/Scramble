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

   **Six things to settle before 2b**, from the adapter's own report:

   1. **`SetNostrEventSigner` throws and every head calls it.** Either build the
      service with an `IAccountIdentityProofSigner` derived from the external
      signer — which needs a kind-450-shaped signing call on `IExternalSigner` —
      or drop the member. `INostrEventSigner` cannot serve it: it picks its own
      `created_at`, so it would sign a different template than the proof commits
      to.
   2. **`StageUpdateAdminPubkeysAsync` throws**, so admin management is
      unavailable until an AppDataUpdate slice exists. `MessageService.UpdateAdminPubkeysAsync`
      fails at runtime.
   3. **`CommitData` now means a finished kind-445 event, not MIP-03
      ciphertext.** `MessageService` must stop calling `EncryptCommitAsync` on
      it, and stop passing `StageRemoveMemberAsync`'s bytes to
      `PublishGroupMessageAsync`. This is the one that breaks loudly.
   4. **`ProcessWelcomeAsync` lost its KeyPackage binding.** The engine's join
      path refuses a Welcome naming a KeyPackage we never published; this
      signature does not carry the kind-30443 event id, so the adapter tries
      each record holding material. HPKE decryption still decides — success is
      proof of possession — but the *fail-closed* check is gone. Fix by passing
      the Welcome's `e` tag (`NostrService` already parses and discards it), or
      the whole gift wrap, which `InboundFanIn` already returns.
   5. **Nothing can tell the service a KeyPackage's published event id** —
      `MarkPublishedAsync` has no caller through this contract, which is *why*
      (4) is currently impossible.
   6. **`IMlsService` is synchronous in eight places that need async storage**,
      bridged by one documented `Blocking<T>`. Worth deciding whether step 3
      makes the interface async, since `IMessageService` is being touched anyway.

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
