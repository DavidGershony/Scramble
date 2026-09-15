# P11 — cutover plan

Drafted 2026-09-15, before any code moves. `CLAUDE.md` calls P11 "the only phase
with real I4/I5 exposure", and the sequencing below exists so that exposure is
decided in advance rather than discovered.

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

## 2. The risk that is actually novel

Not the swap. **The data.** `EncryptedSqliteStorageProvider` wraps
`MarmotCs.Storage.Sqlite`, so every existing group on every installed device is
in marmot-cs's schema, while the new engine has its own (`marmot_`-prefixed,
V001–V010). The plan's row says "migrate or re-key existing local groups" in six
words, and it is the one part with no rollback: a user whose groups do not come
across has lost conversations.

**Decide the strategy before writing any of §3**, because it changes the order:

- **Migrate** — read marmot-cs state, write the new schema. Needs a faithful
  reading of both, and MLS group state is the part that cannot be approximated.
- **Re-key** — treat existing groups as unjoinable and rebuild membership.
  Honest, far simpler, and visibly costly to users.
- **Dual-read** — new engine first, fall back to the old store for groups it
  does not know. Contradicts the "no compatibility shim" scope decision and is
  named here only so it is rejected on purpose rather than by omission.

Whichever is chosen, the test is not "it compiled": it is **an installed device's
database, opened by the new build, still showing its conversations.**

---

## 3. Sequence

Each step is separately verifiable and separately revertible. Steps 1–3 change
no behaviour.

1. **Build the fan-in.** The session layer is per-group; nothing routes an
   inbound envelope to a group. `IRoutingIndexStorage` (rotation-aware routing
   id → group) and `HasTransportSeenAsync` (the duplicate-envelope pre-filter)
   both exist with **no production caller** and are exactly this. Engine-side,
   no Core changes. *Size: S–M.*
2. **Decide and build the data migration**, behind a flag, with the exit test
   above. No service swapped yet. *Size: M, and the whole of the risk.*
3. **Port `IMlsService` onto the session layer.** Twenty members, one
   implementation, no other service touched. This is the cutover proper.
   *Size: M.*
4. **`IMessageService`**, which sits on `IMlsService` and should mostly follow.
   *Size: S–M.*
5. **`INostrService`** — audit rather than port. Most of it is transport that
   does not care which engine is underneath. *Size: S, pending the audit.*
6. **Delete `marmot-cs`** and `Scramble.Core`'s duplicate codecs — the
   duplication the standalone rule created deliberately, which was always to go
   at cutover. *Size: S.*

`ExternalSignerService` is **not** on this list. It is the highest fix-density
file in the repo at 0.59 and it is a signer, not an engine — it should be
touched only if a step above forces it, and then in its own commit.

---

## 4. The invariants, decided now

**I4 — no flag-day rewrites.** Every step above is one subsystem. The step most
likely to breach it is 3, because porting twenty members tempts a single commit.
Split it: the no-op adapter first, verified by the full integration suite, then
the behaviour. If a step genuinely cannot be split, it takes a
`Landing-Discipline-Exempt` trailer naming why — auditable, not silent.

**I5 — pivot freeze.** This is a UI-head migration in everything but name, and
I5 binds: **when the first head cuts over, the other goes bugfix-only** until the
first has an equivalent smoke test green in CI plus one week. Decide which head
leads before step 3. ANALYSIS.md records the 2026-05-11 pivot landing without
this discipline as the worst two-week stretch in the repo's history, at a 4.5×
fix:feature ratio.

**I2** applies throughout — every step touches engine or service paths, so the
integration suite is a merge gate, not an afterthought.

---

## 5. Exit criteria, restated concretely

The plan's row says "Desktop + Android smoke tests green; existing chats still
open; a full-stack E2E passes on both heads." Made testable:

- An **existing device database** opened by the new build lists its groups and
  shows their history. This is the criterion that can fail irreversibly.
- **Both heads** send and receive against a live `wn-agent` peer — the
  `DarkMatterInterop` suite already proves the engine does; this proves the app
  does.
- `SettingsViewModel`'s version string reads the new engine, and nothing else in
  `Scramble.Presentation` names a Marmot type.
- `marmot-cs` is gone from the solution, and the drift check stays clean.

---

## 6. Open questions for the user

1. **Migrate, re-key, or something else** (§2)? Nothing should start before this
   is answered; it sets the order of everything after step 1.
2. **Which head leads** (§4)? I5's freeze lands on the other one the moment it
   does.
3. **Is a beta channel available** for the migration step? An irreversible data
   change is the one place this plan would rather not go straight to users.
