# HANDOFF — Dark Matter migration: you are here

**Updated:** 2026-08-31 (ninth revision) · **Branch:** `feat/dark-matter`
· **Last commit at time of writing:** `918f82d`

Read this first. It tells you exactly what exists, what is next, and how to do
it. It supersedes `step6-build-start-prompt.md`, which described the state
before P0–P2 landed.

**Orientation (read once, in this order):**
1. This file.
2. `ai-tasks/scramble-marmot-phased-plan-2026-08.md` — the authoritative plan.
   §3 is the phase table; §4 the `dotnet-mls` asks; §5 the Whitenoise
   questions; §7 the binding cutover rules.
3. `CLAUDE.md` — repo invariants (I1–I5) and the Dark Matter cutover rules.

---

## 1. The one-paragraph situation

Scramble is replacing its Marmot engine with a standalone Dark Matter
implementation (`Scramble.Marmot.*`), built fresh against Rust `mdk` pinned at
**`wn-agent-v0.9.10`**. Planning is finished. Phases P0 (storage), P1 (epoch
state machine), P2 (account-identity proof) and P4 (app components) are done,
**P3 is closed**, and **P6 has started**: KeyPackage generation, publication
and the first live interop suite all landed on 2026-08-31 (§3f, §3g). Our stack
now validates a KeyPackage the reference implementation actually published, and
reproduces its bytes exactly. Nothing is wired into the running app yet: the new
engine is entirely additive and nothing depends on it, so it cannot break the
shipping product. What remains of **P6** is create-group, join and invite.

---

## 2. What exists now

Seven new projects, all standalone (no reference to `marmot-cs`), all in
`Scramble.sln` and `Scramble.Desktop.slnf`, all running in the fast unit gate.
**748 tests in `Scramble.Marmot.Tests`**, plus **7 in the live
`DarkMatterInterop` suite** (`tests/Scramble.Diagnostics/DarkMatterInterop/`),
all passing.

| Project | Phase | Contains |
|---|---|---|
| `src/Scramble.Marmot.Abstractions` | P0/P1/P3 | Ids (`GroupId`, `EpochId`, `MessageId`, `MemberId`), storage contracts and records incl. `IKeyPackageStorage`, `EpochState`, `ITransportPeeler` + `PeeledMessage` |
| `src/Scramble.Marmot.Storage.Sqlite` | P0/P3 | SQLite provider, migrations, transactions, epoch-anchored snapshots, KeyPackage bundles + private material |
| `src/Scramble.Marmot.Engine` | P1/P6 | `EpochManager`; `KeyPackages/` — leaf shape, lifetime policy, the builder, the publisher, the publication validator. **The only project referencing `dotnet-mls`.** |
| `src/Scramble.Marmot.Identity` | P2 | `AccountIdentityProof` (`0x8009`), async signer seam |
| `src/Scramble.Nostr.Crypto` | P2/P3 | BIP-340, NIP-01 event ids and envelope serialisation, NIP-44 v2, NIP-59 gift wrap, ChaCha20-Poly1305 envelope, secp256k1 |
| `src/Scramble.Marmot.Wire.Nostr` | P3 | kind-445 codec, kind-444 Welcome, kind-30443 KeyPackage, `NostrGroupPeeler` |
| `src/Scramble.Marmot.AppComponents` | P4 | id registry, QUIC-varint codec, the four v1 schemas, app-data dictionary, Current-profile invariants, commit authorization, staged-commit component integrity, the shared relay-URL profile |

Also landed: `dotnet-mls` is **released and pinned at `v0.1.0-beta.9`**
(2026-08-31), which carries everything P6 needs — see §3e. **407 of its tests
pass**, RFC 9420 vectors included. The submodule sits exactly on the tag; keep
it that way. Also a `wn-agent` interop peer in `docker-compose.test.yml`.

---

## 3. Do this next

### 3a. The crypto review is DONE and actioned — nothing to do here

An independent review ran on 2026-08-24 and all five of its defects are fixed
(`bd5d630`, `d44f254`, `30282fa`). Do not re-commission it. What it found, so
the reasoning is not lost:

1. **kind-445 events were never verified.** The id and Nostr signature are now
   checked before any field is trusted, and the transport id is bound to the
   verified hash — it keys deduplication, so an attacker-chosen value let a
   legitimate message be dropped as a duplicate.
2. **Emoji produced the wrong event id.** Every .NET JSON encoder escapes above
   the BMP as surrogate pairs; NIP-01 requires verbatim. The serialiser is now
   hand-written. **Do not replace it with `Utf8JsonWriter`** — no encoder
   setting fixes this.
3. **Malformed input escaped as the wrong exception type**, bypassing the
   retryable/terminal classification the engine branches on. Parsing is total now.
4. **NIP-44 lacked the 2026-06-28 extended length prefix**, so large messages
   could be neither sent nor read. `nip44.vectors.json` has not been regenerated
   since the amendment, so the vector file cannot catch this — the boundary
   vectors inline in `44.md` are what pin it.
5. **Relay URLs accepted userinfo, fragments and duplicates**, contrary to the
   Marmot profile.

It also verified as sound, against external vectors: all 15 BIP-340 cases, all
35 NIP-44 conversation keys, the account-proof fixture byte for byte, the
gift-wrap trust model, and the kind-445 tag shape against upstream.

**Still open, deliberately:** no key-material zeroization anywhere (the reviewer
noted rather than escalated it; it is consistent with the previous
implementation). Worth scoping properly rather than sprinkling.

**The lesson worth keeping:** one test asserted the defect *was* the behaviour,
because the fixture it used could only ever exercise the rejection path. When
code and tests share an author, the tests are not an oracle — pin to external
vectors, and get fresh eyes on cryptography.

### 3b. P3 is DONE, including its interop exit criterion

`c72dd91` added the kind-30443 codec and `f2143ee` the KeyPackage storage.
Both are green in the fast unit gate. What landed:

- Build (unsigned template) and verify-then-parse kind-30443 events, with the
  seven-tag cardinality rules, id-list spelling (`0x` + four lowercase hex
  digits, exact-string compared), and the `app_components`-must-carry-`0x8009`
  rule. Plus the transport candidate ranking (recency, then event id within a
  slot, then decoded KeyPackageRef across slots).
- No `encoding` tag in either direction. Inbound it is inert rather than
  rejected: kind 30443's tag set is **not closed** the way kind 445's is, so an
  unknown tag is carried past. Do not "tighten" this — rejecting unknown tags
  here invents a rule the spec does not state.
- `IKeyPackageStorage` + the SQLite table, keyed by KeyPackageRef with a second
  lookup by kind-30443 event id (what a Welcome actually names). Private
  material is persisted, and erasure is a one-way, idempotent transition; `Put`
  inserts and never replaces, so a stale record cannot resurrect erased key
  material.

**The two P3 bullets that needed a decoded MLS KeyPackage are now DONE** — they
landed with P6's first slice (§3f), which is where the first `dotnet-mls`
reference arrived:

- Attaching the `0x8009` proof to the LeafNode and advertising it in the leaf's
  own support list — `MarmotLeaf`.
- The two checks the codec deliberately surfaced instead of performing:
  KeyPackageRef equality against the decoded KeyPackage, and binding the event
  author to the credential identity — `KeyPackagePublicationValidator`. They
  have a caller now.

**P3 is now closed.** Its last exit criterion — a `DarkMatterInterop` test
against a live `wn-agent` — landed in `909a9d9`. See §3g for what the peer
confirmed and what it corrected. The remaining direction, an agent *validating*
a KeyPackage we published, needs the invite path and arrives with create-group.

### 3c. P4 is DONE — next is P6

Landed: `6152a59` codec primitives, `c180392` the four v1 schemas, `648c751`
the app-data dictionary and Current-profile invariants, `37b076f` the routing
index, `d0a3f38` commit authorization, `2deab4c` the relay-URL dedupe,
`240ea0d` the two GroupContext refusals below, and `4a5a96e` the staged-commit
integrity rule that closed the phase.

Built the v1 set: `0x8001` profile, `0x8003` admin-policy, `0x8004` routing,
`0x8005` retention, `0x8009` proof carriage — and, since `918f82d`, `0x800c`
group-lifecycle. Media (`0x8002`/`0x8008`/`0x800b`), QUIC (`0x8006`) and avatar
(`0x8007`) stay deferred to P12 — **do not build them**, and note that
`CurrentProfile.KnownGroupComponents` deliberately excludes them so a group
requiring one is rejected rather than joined-and-ignored.

> **`0x800c` was deferred here and that was wrong — see §3h.** It is in
> upstream's `default_group_components()`, so every group a `wn-agent` creates
> requires it and every invitee must advertise it. Deferring it blocked create,
> join and invite in both directions. The lesson generalises: **"deferred" is
> only safe for a component nothing else makes mandatory, and
> `default_group_components()` is where to check.** The three still-deferred ids
> above were re-checked against it and are genuinely optional.

**Two encoding facts that will bite whoever touches this next.**

1. **MLS varints are not QUIC varints.** MLS vector lengths (RFC 9420 §2.1.2)
   use 1, 2 or 4 bytes and treat the 8-byte form as invalid, capping at
   2^30-1; the QUIC varint the component payloads use internally allows all
   four widths, and mdk's helper is literally called `encode_quic_varint`. They
   agree at every realistic size, so a mix-up is invisible until it is not. The
   dictionary goes through `AppDataDictionary.WriteMlsLength`; component
   payloads go through `ComponentCodec.WriteVarint`. Keep them apart.
2. **64 is where a varint widens to two bytes.** Two 32-byte admin keys are
   exactly 64 payload bytes. A hand-built fixture using a literal `64` for the
   length prefix decodes as the one-byte value 0 — which still throws, just for
   the wrong reason. Build test fixtures through the codec.

**The rule the whole subsystem turns on:** producers canonicalise, decoders
reject. `Create()` sorts and deduplicates because nothing is committed yet;
`Decode()` refuses the same input. This is signed group state, so a member that
quietly repairs what it was given holds a canonical form nobody else has. The
admin list is the worst case — two members would disagree about who governs the
group while both believed their state valid.

**P4's last item landed in `4a5a96e`** —
`validate_app_component_integrity_for_staged_commit`, as
`AppComponentIntegrity`. Two rules that must be run **as a pair**, because
either alone leaves half the door open:

- `ValidateStagedCommit` takes the diff. The dictionary and the requirement
  list may never disappear, nor may the state of anything the **resulting**
  epoch requires — resulting, so one authorized commit can still unrequire and
  remove an optional component atomically. Every other changed entry must be
  accounted for by one of the commit's own `AppDataUpdate` operations, matched
  on the **resulting value**, not merely on the component id.
- `ValidateUpdateBatch` takes the operations: one per component, requirement
  list resolved across the whole batch before any removal is judged against it,
  payloads decoded under their schemas. Without it, corrupt bytes inside a
  perfectly "update-backed" change sail through.

The hole being closed is upstream's, not ours: MLS's own guard checks the
resulting dictionary against a commit's `AppDataUpdate` proposals and **returns
early when there are none**, so a `GroupContextExtensions`-only commit can swap
the admin set or drop the `app_data_dictionary` outright and MLS accepts it.

The operation hangs off `StagedProposal` rather than a second list beside it,
so an engine caller cannot fill one and forget the other; a proposal staged
without its operation is refused rather than read as a no-op. Both rules sit
behind `StagedCommitView`, so **P6's engine has to actually call them** — they
are pure functions with no caller yet, exactly like the two KeyPackage checks
in §3b. Do not let them fall off either.

**Two fail-open divergences found while porting and fixed** (`240ea0d`); both
would have had us join a group every current peer refuses, then sit in it
alone:

- `safe_aad` (`0x0002`) had been treated as presence-only. Upstream refuses
  those bytes as GroupContext state. It stays a *known* component — an unknown
  optional component is still carried opaquely; this one is an error.
- Encrypted-media v1 (`0x8008`) is **frozen, not deferred**: a Current-profile
  group may neither require it nor hold its state. Required, it already failed
  as unsupported; merely present, it fell through as an unknown optional id.
  It is the one id named without a codec behind it, and only so it can be
  refused.

**It is NOT permission-gated on `dotnet-mls`, despite looking like it should
be.** Checked in the source on 2026-08-25 rather than inferred, and the answer
is more specific than "needs staged-commit introspection":

- **dotnet-mls has no *inbound* staging at all.** `ProcessCommit` applies
  directly (`MlsGroup.cs` ~line 1193, "Apply the new state"). Staging exists
  only for *outbound* commits (`Commit` → `MergePendingCommit` /
  `ClearPendingCommit`), and `PendingCommitState` is `internal` besides.
- **But rollback works, with zero library changes.** `Export()` and `Import()`
  are public. So: snapshot, `ProcessCommit`, inspect the resulting dictionary,
  and re-`Import` the snapshot if the commit is Marmot-invalid. This is the same
  primitive the convergence deep-dive identified for snapshot/restore, applied
  to a different problem.
- **A throw inside `ProcessCommit` is already safe.** It computes into
  `tentative*` locals and assigns only at the end, so an MLS-invalid commit
  leaves state untouched. Rollback is needed only for commits that are
  MLS-valid but Marmot-invalid — a much narrower path than it first looks.
- **The round-trip is lossy in exactly two places, and neither matters here.**
  `Export()` omits `_proposalCache` — recoverable by re-caching, and
  `ProcessCommit` clears it anyway — and `_resumptionPsks`, which **Marmot v1
  does not use**. Checked, not assumed: the spec repo contains no occurrence of
  "resumption"; PSK appears only in `group-lifecycle-v1.md` (`0x800c` — its
  one-byte *state* is now implemented, §3h; the disband protocol is still P12) and the `features/multi-device.md` draft, which is marked "Status:
  branch draft" and says its bytes MUST NOT be implemented for interop yet —
  and which uses an **External** PSK (`MLS-Exporter("marmot", join_psk_id,
  KDF.Nh)`), not a resumption PSK. **So the rollback route is clean and P4's
  last item needed no library change** — and did not, in the end, need any MLS
  introspection at all: the rule is a pure function of two dictionaries and a
  proposal list, which is what the view types were for.

**Conformance vectors are live** (`9e1ea21`). Upstream's byte fixtures are
mirrored verbatim in `tests/Scramble.Marmot.Tests/vectors/marmot/` and run
under `Category=ConformanceVector` in the fast unit gate. They are the only
tests here not written by the author of the code they check. Three of them
matter beyond decoding: our encoder must reproduce upstream's bytes exactly
(reading them proves nothing about being read); the `component_data_hex` entry
independently pins the dictionary framing, which has no Marmot spec prose of
its own; and the Current-profile constants are compared against upstream's
declared contract rather than merely hardcoded.

**If a vector starts failing after a pin bump, that is the signal it exists to
give.** Refresh the fixture from the new tag deliberately and read the diff —
never edit one to make it pass.

Only the byte fixtures are mirrored. Upstream's scenario vectors
(`invite-member`, `convergence-*`, …) drive a whole engine through a step list
and become runnable at P6.

### 3e. `dotnet-mls` is released at `v0.1.0-beta.9` — nothing is blocked

**Status (2026-08-31):** merged to `main`, tagged, pushed, and the Scramble
submodule is pinned exactly on the tag (`ccb9720`). Three changes, all generic
RFC 9420 — no Marmot constant crosses the boundary. **407 tests pass** (341 +
66 crypto).

The two that were already implemented and awaiting release:

**(1) `AppDataUpdate` was wire-format only; the group never applied it.** The
interop-fatal one. `47bb6d2` added the proposal struct, its codec and its
`ProposalType`; `MlsGroup` was not touched, so in both `Commit` and
`ProcessCommit` the proposal chain had no arm for it and no final `else`, and
it was silently dropped. The confirmation tag is computed over the resulting
GroupContext, so **any commit carrying an `AppDataUpdate` failed its tag check
on our side** — a rejected commit, not a lost update. Not a corner case: read
off `message_processor/send.rs` at `wn-agent-v0.9.15`, mdk couples an
admin-policy `AppDataUpdate` into the same commit on invite-with-admin-grant
(:237) and on removing a member who is an admin (:543). Both are P6 exit
criteria.

**(2) A Marmot KeyPackage could not be built from outside the library.**
`CreateKeyPackage` hardcoded empty leaf extensions and took no proposal
capabilities, and `SignLeafNode` is private. Reimplementing LeafNodeTBS signing
on our side would have duplicated security-critical serialisation the library
owns and diverged silently the first time either changed.

**How (1) and (2) were fixed.** The dictionary is now a first-class
`AppDataDictionary` type in `DotnetMls.Types` — a vector of
`{uint16 component_id, opaque data<V>}`, ordered by id, one entry per id, both
enforced on decode — and both commit paths apply the proposals to it. The
application rule follows OpenMLS's `extensions-draft` behaviour exactly,
because that is what `wn-agent` runs: the dictionary comes from the **current**
GroupContext, is updated, and is written into the resulting extension set, so a
commit carrying both a `GroupContextExtensions` proposal and `AppDataUpdate`s
takes its extensions from the former and its dictionary from the latter.
`CreateKeyPackage` and `CreateGroup` both take `leafExtensions` and
`supportedProposalTypes` now, and union a leaf's carried extension types into
its advertised capabilities.

**(3) The lifetime blocker, found on 2026-08-31 and not previously recorded.**
`CreateKeyPackage` hardcoded `Lifetime(0, ulong.MaxValue)` with no override.
`wn-agent` runs `validate_key_package_lifetime_policy` -> OpenMLS
`has_acceptable_range()`, which caps `not_after - not_before` at
`MAX_LEAF_NODE_LIFETIME_RANGE_SECONDS` — **7,261,200s**, read off
`openmls/src/key_packages/lifetime.rs` at `erskingardner/openmls@59e7d3b2`, the
exact revision mdk pins. So **every KeyPackage we published would have been
rejected before anything else about it was looked at**. No workaround existed:
the window is signed inside LeafNodeTBS and `SignLeafNode` is private.
`CreateKeyPackage` now takes an optional `Lifetime`, defaulting to the old
unbounded value so existing callers are unaffected, and refusing an empty or
inverted window up front. `CreateGroup` needed nothing — its leaf is
`LeafNodeSource.Commit`, which carries a parent hash rather than a lifetime.

**One honest note on the earlier work.** The sort into component-id order
survived mutation: operations on distinct ids commute and the sort is stable,
so it cannot change the result. It is kept for legibility against the reference
implementations and the XML doc says so rather than letting it take credit.

**The asks still open, unchanged** (plan §4):

- **(b) SelfRemove — real, ask at P7.** `0x000a` is not expressible; the closed
  `ProposalType` enum stops at `AppDataUpdate = 8`. No workaround exists for a
  proposal type that cannot be encoded.
- **(d) retained past-epochs — probably avoidable, do not ask yet.** A
  per-epoch `Export()` yields the `(KeyScheduleEpoch, SecretTree)` pair it asks
  for. One question is unsettled: ratchet advancement on decrypt from a restored
  snapshot. Blocks P8, so there is time to settle it.
- **(f) staged-commit introspection — not a blocker**, as plan §4 always said.
  `Export()`/`Import()` rollback covers it. `ProcessCommit` computes into
  `tentative*` locals and assigns only at the end, so an MLS-invalid commit
  already leaves state untouched; rollback is needed only for commits that are
  MLS-valid but Marmot-invalid. The round-trip is lossy in exactly two places
  and neither matters: `Export()` omits `_proposalCache` (recoverable, and
  `ProcessCommit` clears it anyway) and `_resumptionPsks`, which **Marmot v1
  does not use** — checked, not assumed.

**A new ask is coming at the invite path, and it is real.** `dotnet-mls`
**signs** a KeyPackage and a LeafNode and exposes **no way to verify either** —
there is no KeyPackageTBS verification anywhere in the library. The
account-identity proof binds the account key to the leaf *signature* key, and
we verify that, so identity binding is covered. What is not covered is
**possession of the leaf private key**: a party who copies a valid leaf and its
proof and substitutes their own `init_key` would receive the Welcome. See §3f.

**Re-check whether an ask is still real before spending a permission on it.**
Two of the earlier three dissolved on a source read costing minutes, and a
permission granted for something unnecessary is worse than not asking — it
invites the change to be made.

### 3f. P6 has started — KeyPackage generation is DONE

`c36e7da` landed the first P6 slice in `src/Scramble.Marmot.Engine/KeyPackages/`
(22 tests, mutation-checked). It is also the first `dotnet-mls` reference from a
`Scramble.Marmot.*` project — everything below the engine was buildable without
MLS types, which is why it came first.

What landed:

- **`MarmotLeaf`** — the leaf capability set and the leaf `app_data_dictionary`.
- **`KeyPackageLifetimePolicy`** — the window, and the bound applied to inbound
  KeyPackages too.
- **`MarmotKeyPackageBuilder`** — mints a fresh leaf signature key, signs the
  `0x8009` proof through the async signer seam, builds the KeyPackage, computes
  the ref, frames the published bytes, and hands back a `KeyPackageRecord`.
- **`KeyPackagePublicationValidator`** — the two checks §3b said must not fall
  off. They now have a caller.
- **`KeyPackagePrivateMaterial`** — init + leaf HPKE + signature private keys,
  versioned and length-prefixed. All three, because `ProcessWelcome` takes all
  three.

**Facts worth not rediscovering, all read off `wn-agent-v0.9.15` rather than
spec prose:**

- Current-profile **leaf capabilities** are extensions `{0x0003, 0x0006}` and
  proposals `{0x0008}`. **`0x8009` is NOT an advertised extension capability in
  Current** — that was Legacy's shape, and advertising it is a Legacy tell.
- The **leaf's `app_data_dictionary` has three entries**, not one: `0x0001`
  (the advertised component list, with `0x0001` and `0x8009` unioned in),
  `0x0002` `safe_aad` **with an empty component list**, and `0x8009` (the
  proof). The middle one surprises people: an empty `safe_aad` entry in a
  *leaf* is what upstream emits, and it is **not** the same thing as `safe_aad`
  appearing in a *GroupContext* dictionary, which `240ea0d` made an error.
- **The leaf signature key is fresh and is not the Nostr account key.** The
  credential identity is the account key; the proof is what binds the two. They
  could not be the same key — different signature schemes.
- The proof is validated against the KeyPackage's **own** ciphersuite, never a
  default (mdk#747).
- **KeyPackageRef is `hash_ref` over the KeyPackage struct**, while the
  published bytes are an `MLSMessage` wrapping it. Hashing the envelope yields a
  reference nobody else computes.

**Two deliberate divergences from upstream. Do not "fix" either without reading
this:**

- **Nothing is marked last-resort.** mdk marks every KeyPackage it generates,
  because OpenMLS otherwise deletes the private bundle the first time a Welcome
  consumes it. We own our storage and delete nothing implicitly, and
  `dotnet-mls` cannot set KeyPackage-level extensions at all. **The consequence
  is real:** kind 30443 is a replaceable event, so two people can invite us off
  one publication and only the first Welcome will open. **§3g corrects the
  encoding — it is a component, not an extension — and confirms the live peer at
  our pinned tag does mark it.** Read that before acting on this bullet.
- **The validator does not verify the KeyPackage or LeafNode signatures**,
  because `dotnet-mls` offers no way to. See §3e. **This must close before the
  invite path treats a fetched KeyPackage as trustworthy** — it is the one hole
  left in the fetch path. It is documented in the validator's own XML docs so
  whoever writes that path cannot miss it.

**Publishing landed too** (`ca93ff4`): `KeyPackagePublisher` builds, persists
and publishes, then binds the record to its event id. The ordering is
persist-then-publish and the failure handling is the substance — the relay seam
reports **three** outcomes, and only `Rejected` authorises deleting the record.
A transport that throws is read as `Indeterminate`, because an exception says
nothing about what the relay saw. Erasing the private key for a KeyPackage that
did reach a relay is unrecoverable; accumulating a few dead records is not.
The slot is derived from the newest existing record, so a republish supersedes
rather than accumulates.

**What P6 needs next**, roughly in order:

1. **Create group / join.** The big one, and the next real milestone. It is
   **unblocked now** — `0x800c` was the thing standing in front of it (§3h).
   `AppComponentIntegrity.ValidateStagedCommit` and `ValidateUpdateBatch` are
   still pure functions with no caller (§3c) — **P6's engine has to actually
   call them, as a pair.** The initial GroupContext is
   `required_capabilities` + an `app_data_dictionary` holding the requirement
   list, the group profile, the admin policy and the lifecycle state; read
   `app_data_dictionary_extension_for_group` and `do_create_group` before
   writing it, exactly as §3h did.
2. **Invite, which needs two `dotnet-mls` additions** (§3e, §3g): KeyPackage
   and LeafNode signature verification before a fetched package is trusted, and
   KeyPackage-level extensions so we can mark last-resort. Neither is large.
3. **The outbound interop direction** — a live `wn-agent` validating a
   KeyPackage *we* published, which the invite path unlocks. §3g covers the
   inbound direction only.

### 3g. Interop is LIVE — what the reference peer confirmed and corrected

`909a9d9` landed the first `DarkMatterInterop` suite, closing P3's last exit
criterion. It bootstraps the `wn-agent` container, fetches its kind-30443 event
off the relay, and runs our stack over bytes the reference implementation
produced. **Six tests, green against `wn-agent-v0.9.10`, mutation-checked.**

Run it with:

```powershell
docker compose -f docker-compose.test.yml up -d --build wn-agent
dotnet test tests/Scramble.Diagnostics/ --filter "Category=DarkMatterInterop"
```

**The assertion that carries the weight** is not that we can decode upstream's
KeyPackage — a decoder that silently drops a field it does not understand still
decodes. It is that **re-encoding the decoded KeyPackage and hashing it
reproduces upstream's own `i` tag**. That makes the round trip byte-exact, and
it is the only cheap way to prove a codec against a peer.

**Confirmed, having previously only been inferred:**

- The leaf `app_data_dictionary` carries exactly three entries, and `safe_aad`
  is present and **empty**. The asymmetry with GroupContext state is real.
- The advertised component list unions in `0x0001` and `0x8009`.
- `0x8009` is absent from leaf extension capabilities in Current.
- **The lifetime range is exactly 7,261,200 seconds** — the bound derived by
  reading the OpenMLS revision mdk pins, now observed on the wire. This is the
  assertion that would have caught the unbounded lifetime we used to emit,
  before a peer ever saw it.

**Corrected, and this one matters:**

**Last-resort is a component, not an extension.** The reference marks its
KeyPackage last-resort as component **`0x0004` with EMPTY data inside the
KEYPACKAGE-level `app_data_dictionary`** — not a leaf extension, and not the
obsolete `0x000a` extension. Confirmed in
`openmls/src/key_packages/mod.rs::KeyPackage::last_resort` at the pinned
revision, which also treats **non-empty data there as malformed**. The agent at
our own pinned tag does mark it, so this is a divergence from `v0.9.10`, not
only from `0.9.15` as §3f originally implied.

We still cannot emit it: `MlsGroup.CreateKeyPackage` hardcodes an empty
KeyPackage-level extension set and offers no parameter. The interop test asserts
both sides, so it fails deliberately the day the library grows one. **This is
the next `dotnet-mls` ask** — the same shape as the `leafExtensions` and
`lifetime` parameters already added, and smaller than either.

**Differences that are expected and are NOT bugs:**

- The agent advertises extensions `0xf2d1`, `0xf2d2`, `0xf2d4` from its feature
  registry, and proposal `0x000a` (SelfRemove). We advertise neither. The test
  asserts only that **our** set is a subset of theirs, which is the direction
  that has to hold.
- The agent's `app_components` tag lists `0x8001`–`0x800c`; ours lists the
  Known set. Its leaf list also contains `0x0001` while its tag does not.

**The suite skips when the container is absent**, because a missing container is
an absent environment, not a regression. That means a failed image build would
turn it green-but-empty, so `integration.yml` has an explicit readiness check
after the build. **Do not remove it as redundant** — it is the only thing
standing between a broken peer and a silently passing suite.

**One cost, named rather than buried:** building the peer from a pinned mdk ref
is now the slowest step in `integration.yml` (a cold cargo release build). If
that makes PRs slow, move the suite to a nightly **Ubuntu** workflow rather than
dropping it — the existing nightly is Windows and cannot run the container.

### 3h. `0x800c` was deferred and should not have been — read this before create-group

`918f82d` implements the group-lifecycle component. It was scoped out at P4 as
a P12 item, and reading `do_create_group` before writing create-group is what
caught the mistake.

**Why the deferral was wrong.** `0x800c` is in upstream's
`default_group_components()`, beside the group profile and the admin policy. So:

- **Every group a `wn-agent` creates requires it.** While it was absent from
  `CurrentProfile.KnownGroupComponents`, our validator refused every such group
  as requiring something unsupported — which is to say, all of them.
- **Every invitee must advertise it.** `do_create_group` computes
  `mandatory_components` as `default_group_components()` plus the account proof
  and refuses any invitee whose leaf omits one. No `wn-agent` could have invited
  us into any group.

Both interop directions, blocked, by a component we had written off.

**Why it looked deferrable, so the same reasoning is not repeated.** The disband
*protocol* is genuinely heavy, and `group-lifecycle-v1.md` is the one v1
document that mentions PSKs — which is what put it in the same mental bucket as
media and QUIC. The *state* is one byte: `0` active, `1` disbanded. The protocol
is still P12; the state is not.

**The encoding is stricter than the obvious reading, in three ways.** Upstream's
own test pins all of them, and each is a case a lenient decoder waves through:

- **An empty payload is an error, not the default.** Tempting, since `Active` is
  `0`. A group we alone consider active is the worst outcome available.
- **An unknown value is an error, not something to carry.** Unlike an unknown
  *component*, which stays opaque, this one decides whether the group is usable
  at all — guessing is worse than refusing.
- **A trailing byte is an error, not padding.**

**It is deliberately NOT in `RequiredComponents`.** Upstream's
`CURRENT_PROFILE_REQUIRED_APP_COMPONENTS` is `{0x8003, 0x8009}` and nothing
else. `0x800c` becomes required through group *creation*, not through the
profile, so a group that does not require it is still valid and must not be
refused for lacking it. Conflating the two would reject legitimate groups.

**The generalisable lesson, which is the reason this section exists:**
**"deferred" is only safe for a component nothing else makes mandatory, and
`default_group_components()` is where to check.** The three ids still deferred
(`0x8002`/`0x8008`/`0x800b` media, `0x8006` QUIC, `0x8007` avatar) were
re-checked against it and are genuinely optional.

**The interop suite now closes the loop**: it asserts our advertised set covers
every component the running peer treats as mandatory at create time, read off
the peer's own published leaf rather than trusted from a source file.
Re-deferring `0x800c` makes it fail — verified.

**Three existing tests pinned the old deferral and failed**, which is the right
failure. They were re-pointed at components that are still genuinely deferred
rather than weakened. If a future scope change makes one of them fail again,
re-point it the same way; do not delete the assertion.

### 3d. Non-code items still open (not blocking)

- **Open a PR for `feat/dark-matter`.** **54 commits** ahead of `master` and
  growing; it is reviewable now and will not be after P6. This is the repo's
  documented flag-day failure mode (I4). *(The user has said a PR is not wanted
  — the plan is to keep going and merge at the end. Recorded here because I4
  names exactly this shape as the risk, not to re-litigate the decision.)*
- ~~**Merge `feat/generic-mls-additions`**~~ **✅ DONE (2026-08-25)** — reviewed,
  merged fast-forward into `main`, released as `v0.1.0-beta.8`.
- ~~**Release the `dotnet-mls` P6 blockers**~~ **✅ DONE (2026-08-31)** — merged
  fast-forward into `main`, released as `v0.1.0-beta.9`, submodule pinned on the
  tag.
- **Decide the last-resort question.** Interop settled the *encoding* (§3g);
  what is left is whether to ask `dotnet-mls` for KeyPackage-level extensions
  now or accept one-Welcome-per-publication until invite needs it.
- **Watch the interop step's cost in CI.** It is the slowest step in
  `integration.yml`; §3g says what to do if it becomes the reason PRs are slow.
- **Send Whitenoise the questions** in plan §5 (deployed tag, flip date,
  wire-stable tag, disband-for-interop). Q2 on legacy proofs is closed.

---

## 4. How to work here

**Commands.**

```powershell
# Fast unit gate — includes every Scramble.Marmot test
dotnet test Scramble.Desktop.slnf --filter "Category!=Relay&Category!=Integration" -p:DesktopOnly=true

# Just the engine tests
dotnet test tests/Scramble.Marmot.Tests

# Integration gate (needs Docker; required by CI on engine paths)
docker compose -f docker-compose.test.yml up -d nostr-relay
dotnet test tests/Scramble.Diagnostics/ --filter "Category=Integration|Category=MIP-Compliance|Category=ProtocolCompliance|Category=FullE2E|Category=EpochSync|Category=DeviceSync|Category=OutboxModel|Category=Notifications|Category=RelayHarness|Category=ExporterSecret"

# The Dark Matter interop suite. The peer builds from a pinned mdk ref, so the
# first run is slow; the tests SKIP when it is not up rather than failing.
docker compose -f docker-compose.test.yml up -d --build nostr-relay wn-agent
dotnet test tests/Scramble.Diagnostics/ --filter "Category=DarkMatterInterop"

# Driving the peer by hand (it bootstraps itself in the tests)
docker exec wn-agent-interop wn-agent bootstrap --home /data/marmot-agent `
  --socket /run/marmot-agent/wn-agent.sock --no-quic --json

./scripts/check-drift.ps1
```

**Adding a project.** Add it to `Scramble.sln`, to `Scramble.Desktop.slnf` (or
it will not run in the unit gate), and to the path triggers in
`.github/workflows/integration.yml` (invariant I2 — engine code must not land
without the interop suite running).

**Rules that bind, and why.**
- **Standalone.** No reference to `marmot-cs` from any `Scramble.Marmot.*`
  project. Codecs are ported in. This duplicates code still in
  `Scramble.Core`; that is intended, and Core's copies go at cutover (P11).
- **No Marmot types in `Scramble.Presentation`.** Engine types stop at the
  service layer.
- **Generic Nostr crypto stays out of Marmot namespaces** — it lives in
  `Scramble.Nostr.Crypto` so a future non-Marmot provider can reuse it.
- **`lib/dotnet-mls` needs explicit permission per change.** Everything P6 needs
  is granted, done, and released as **`v0.1.0-beta.9`** (§3e) — **reference the
  tag, never the branch**, and keep the submodule pinned exactly on it. What is
  still open, and the one ask that is coming, are in §3e.
  **Re-check whether an ask is still real before spending a permission on it.**
  Two of the earlier asks dissolved on a source read costing minutes, and a
  permission granted for something unnecessary is worse than not asking — it
  invites the change to be made. Equally: **read the peer's validation path, not
  only the spec.** The lifetime blocker was invisible from the Marmot documents
  and obvious from twenty lines of OpenMLS.
- **Legacy `0xf2f1` account-identity proof is out of scope** (decided
  2026-08-10). Build only Current `0x8009`. Do not re-open it.
- **Safe-export is resolved and dropped from v1** (plan §4). Do not re-open it.
- **Small commits** (I4) and **tests land with their code** (I3).

**Verify, do not assume.** Three habits have repeatedly earned their keep here:

- **Mutation-check a regression test** by breaking the fix and confirming the
  test fails. It has caught weak tests several times — including one that
  asserted a limit existed without pinning where it was, and one whose fixture
  could only ever reach the rejection path.
- **A guard that survives mutation is not automatically the guard doing the
  work.** `last_epoch IS NOT NULL` in the routing prune looked load-bearing and
  is not — SQL's three-valued logic already excludes the row. Say so in the
  comment rather than letting it take credit.
- **Never trust an exit code alone.** `docker compose build` was observed
  exiting 0 having reached neither the daemon nor the registry.

---

## 5. Traps already paid for — do not rediscover these

| Trap | What happens | The rule |
|---|---|---|
| `NBitcoin.Secp256k1.SigVerifyBIP340` | Broken on .NET Android only | Use the BouncyCastle BIP-340 implementation in `Scramble.Nostr.Crypto`. Do not "simplify" it back to the library call. |
| `encoding` tag on 445/444/30443 | Current peers reject the whole event before any MLS processing | Never emit it. kind-445 carries exactly one `h` tag and at most one `expiration`, nothing else. |
| Assuming kind 30443 has a closed tag set like 445 | Rejecting an unknown tag invents a rule the spec does not state, and breaks against a peer that adds one | 445 says "no other tag"; 30443 constrains its seven tags and is silent on others. Carry unknowns past. Duplicates of the seven are still fatal. |
| Reading only the first of a repeated tag | An attacker prepends a value another implementation ignores, so two peers disagree about one signed event | Explicitly a MUST NOT. Reject the event. |
| Uppercase hex in tags | Decodes identically but changes the event id | Lowercase everywhere on the wire. |
| Dedup on the Nostr event id | Same MLS message under a different envelope is processed twice | Dedup on `MessageId.FromMlsBytes`; transport ids are a pre-filter only. |
| `wn-agent` + private-range relay | `connector request failed`, nothing in the agent log | The agent accepts plaintext `ws://` only for a *literal* loopback host, hence `network_mode: host` and `ws://127.0.0.1:7777`. |
| `wn-agent` socket dir at `0755` | `PermissionDenied` naming no path | The socket's parent directory must be `0700`. |
| `wn-agent serve` | `unrecognized subcommand` | There is no `serve`. Running `wn-agent` bare is what serves. |
| Git Bash + `docker exec` | Paths rewritten to `C:/Program Files/Git/...` | Prefix with `MSYS_NO_PATHCONV=1`. |
| Submodule left on another branch | `Scramble.Core` fails to compile with a missing symbol | `git submodule update --init --recursive` restores the recorded commit. |
| `Utf8JsonWriter` for NIP-01 canonical form | Emoji get surrogate-escaped, so the event id differs from everyone else's | Use the hand-written serialiser in `NostrEventTemplate.Serialize`. No encoder option fixes it. |
| Pinning only to `nip44.vectors.json` | Passes while missing the 2026-06-28 amendment the file predates | Check `44.md` prose and its inline vectors too. |
| Using a QUIC varint for an MLS vector length | Agrees at every realistic size, then silently diverges past 2^30 | MLS allows 1/2/4 bytes only. `AppDataDictionary.WriteMlsLength` for MLS lengths, `ComponentCodec.WriteVarint` inside component payloads. |
| Guessing a component id from its name | `0x8002` is the Blossom *image* component; encrypted-media v1 is `0x8008`. Freezing the wrong id would refuse a legal group and admit a frozen one | Read the constant out of `crates/traits/src/app_components/mod.rs`. Every id in `AppComponent` traces to a line there. |
| Deferring a component because its protocol looks heavy | `0x800c`'s state is one byte, but it is in `default_group_components()` — so deferring it blocked create, join and invite in both directions | Check `default_group_components()` before calling anything optional. "Deferred" is only safe for what nothing else makes mandatory. |
| Assuming last-resort is an MLS extension | It is component `0x0004` with EMPTY data in the KEYPACKAGE-level `app_data_dictionary`; `0x000a` is the obsolete form, and non-empty data is malformed | Read `KeyPackage::last_resort` in the OpenMLS revision mdk pins, not the extension registry. |
| Treating a skipped interop suite as a passing one | The tests skip when the container is absent, so a failed image build reads as green | `integration.yml` has an explicit readiness check after the build. Do not remove it as redundant. |
| Deleting a KeyPackage record because the publish "failed" | A timeout is not a rejection; the event may be live, and erasing its private key is unrecoverable | Only `Rejected` authorises deletion. A throwing transport is `Indeterminate`. |
| A literal `64` as a varint length prefix in a test fixture | Decodes as the one-byte value 0, so the test still throws — for the wrong reason | Build fixtures through the codec, never by hand. |
| Letting `CreateKeyPackage` pick the lifetime | `dotnet-mls` defaults to `(0, ulong.MaxValue)`; `wn-agent` refuses it before reading anything else | Always pass a window from `KeyPackageLifetimePolicy`. The bound is OpenMLS's `MAX_LEAF_NODE_LIFETIME_RANGE_SECONDS`, not a Marmot rule, and no Marmot document restates it. |
| Adding headroom to the KeyPackage validity | The default already sits exactly on the acceptable range; anything above it is refused | The window is `margin + validity` and the peer's check is `<=`. There is no room above. |
| Hashing the published bytes to get a KeyPackageRef | The `MLSMessage` framing adds a version and wire format, so the ref matches nobody | `hash_ref` over the inner `KeyPackage` struct only. |
| Advertising `0x8009` as a leaf extension capability | It is the Legacy profile's shape; a Current peer reads it as a Legacy leaf | Current leaf capabilities are extensions `{0x0003, 0x0006}`, proposals `{0x0008}`. |
| Omitting `safe_aad` from a *leaf* dictionary because it is refused in a GroupContext | Two different surfaces with opposite rules; the leaf must carry it, empty | `0x0002` with an empty component list in the leaf; an error as GroupContext state. |
| A 500-character hostname in a URL-length test | .NET's own host-length limit trips first, so the test asserts the wrong thing | Pad the path instead; the relay profile permits one. |
| Literal U+2028/U+2029 in C# source | They are line terminators — the file will not compile | Build such strings at runtime from char codes. |
| Python `open(...,"w")` then an encode error | Truncates the file to zero bytes | Write to a temp file and move, or use the Edit tool. |

---

## 6. Upstream moves fast — re-check the pin

`mdk` landed ~8 commits/day through mid-2026, and the account-identity-proof
format hard-broke between two tags. Before relying on any reading of upstream:

```bash
gh api repos/marmot-protocol/mdk/tags --paginate -q '.[].name' | head
```

**Checked 2026-08-26: upstream is at `wn-agent-v0.9.15`, five tags ahead of our
`v0.9.10` pin, and the app-component drift diff over 0.9.11–0.9.15 has now been
run** — `crates/cgka-engine/src/app_components.rs` at both tags, whole-file.
Nothing was re-pinned, and nothing needs to be:

- **No component id, rule or encoding changed.** The diff is error *typing* —
  an orphaned-admin refusal moved from the unclassified `Other` bucket to
  `UnknownMember`, naming the key at fault — plus a new `admin_policy_is_empty`
  helper backing an `AdminDepletion` refusal we already have (`AdminPolicy`
  rejects an empty set on both encode and decode).
- The P4 work was therefore read at **0.9.15** and matches 0.9.10 line for line
  in every rule it ports.

**Re-checked 2026-08-31: still `wn-agent-v0.9.15`, no new tags.** P6's
KeyPackage work was read at that tag — `cgka-engine/src/{capabilities,
key_package,identity,app_components}.rs` and, through mdk's `Cargo.toml`,
`openmls/src/key_packages/lifetime.rs` at `erskingardner/openmls@59e7d3b2`. The
OpenMLS revision matters as much as the mdk tag: the lifetime bound lives there
and nowhere in the Marmot documents, so a peer bump can move it invisibly.

If the pin needs to move, do it deliberately: bump `MDK_REF` in
`tests/wn-agent-docker/Dockerfile`, re-run the drift diff over the modules in
plan §2, and record the new pin in the plan doc. An unpinned or silently
bumped reference retargets every interop test at once.

---

## 7. The date band, for context

Wire interop with `wn-agent`: **mid-Nov 2026 / late Dec 2026 / mid-Feb 2027**
(optimistic / expected / pessimistic). Production cutover: **mid-Mar / mid-May /
mid-Sep 2027**. Assumptions and the three risks driving the width are in plan
§6. The band is wide chiefly because of upstream velocity, not because any
single piece is unknown.
