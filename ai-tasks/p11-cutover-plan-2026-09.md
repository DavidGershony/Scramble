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
2. **Port `IMlsService` onto the session layer.** Twenty members, one
   implementation, no other service touched. This is the cutover proper, and
   with the migration gone it is now the first step that changes behaviour.
   *Size: M.*
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
