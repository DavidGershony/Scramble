# HANDOFF — Dark Matter migration: you are here

**Updated:** 2026-09-02 (seventeenth revision) · **Branch:** `feat/dark-matter`
· **Last commit at time of writing:** `eccecdc`

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
shipping product. Create-group landed on 2026-08-31 (§3i), the last two `dotnet-mls` gaps closed
on 2026-09-01 (§3j), and **invite landed the same day (§3k)** — there is a
two-party group with a member who joined through a Welcome. What remains of **P6** is join-from-Welcome, application messages, and the
outbound interop direction, which is **green as of 2026-09-02 (§3m)** — the
reference client joins a group we create.

---

## 2. What exists now

Seven new projects, all standalone (no reference to `marmot-cs`), all in
`Scramble.sln` and `Scramble.Desktop.slnf`, all running in the fast unit gate.
**823 tests in `Scramble.Marmot.Tests`**, plus **13 passing and 2 skipped in the
live `DarkMatterInterop` suite** (`tests/Scramble.Diagnostics/DarkMatterInterop/`),
all passing.

| Project | Phase | Contains |
|---|---|---|
| `src/Scramble.Marmot.Abstractions` | P0/P1/P3 | Ids (`GroupId`, `EpochId`, `MessageId`, `MemberId`), storage contracts and records incl. `IKeyPackageStorage`, `EpochState`, `ITransportPeeler` + `PeeledMessage` |
| `src/Scramble.Marmot.Storage.Sqlite` | P0/P3 | SQLite provider, migrations, transactions, epoch-anchored snapshots, KeyPackage bundles + private material |
| `src/Scramble.Marmot.Engine` | P1/P6 | `EpochManager`; `KeyPackages/` — leaf shape, lifetime policy, builder, publisher, publication validator; `Groups/` — `required_capabilities` codec, component negotiation, group creation, add-members. **The only project referencing `dotnet-mls`.** |
| `src/Scramble.Marmot.Identity` | P2 | `AccountIdentityProof` (`0x8009`), async signer seam |
| `src/Scramble.Nostr.Crypto` | P2/P3 | BIP-340, NIP-01 event ids and envelope serialisation, NIP-44 v2, NIP-59 gift wrap, ChaCha20-Poly1305 envelope, secp256k1 |
| `src/Scramble.Marmot.Wire.Nostr` | P3 | kind-445 codec, kind-444 Welcome, kind-30443 KeyPackage, `NostrGroupPeeler` |
| `src/Scramble.Marmot.AppComponents` | P4 | id registry, QUIC-varint codec, the four v1 schemas, app-data dictionary, Current-profile invariants, commit authorization, staged-commit component integrity, the shared relay-URL profile |

Also landed: `dotnet-mls` is **released and pinned at `v0.1.0-beta.10`**
(2026-09-01), which carries everything P6 needs — see §3e. **415 of its tests
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

### 3c. P4 is DONE — the app-component rules

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

### 3e. `dotnet-mls` is released at `v0.1.0-beta.10` — nothing is blocked

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

**(4) and (5), granted and released as `v0.1.0-beta.10` on 2026-09-01.**
`dotnet-mls` **signed** a KeyPackage and a LeafNode and could verify **neither**
— there was no KeyPackageTBS verification anywhere in it. The gap was specific:
the account-identity proof binds the account key to the leaf *signature* key and
nothing more, so a party who copied a valid leaf and its proof and substituted
their own `init_key` would have received the Welcome, with the proof still
verifying. `ValidateKeyPackage` now runs the three §10 checks that do not depend
on the caller's clock or trust model — LeafNode signature, KeyPackage signature,
and `init_key != encryption_key`. The KeyPackageTBS serialisation is shared
between signing and verification rather than written twice; two copies of a
to-be-signed serialisation is the classic way to ship a verifier that accepts
what nothing else produces. `CreateKeyPackage` also takes
`keyPackageExtensions`, which is what the last-resort marker needs.

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

- ~~Nothing is marked last-resort~~ **✅ CLOSED (2026-09-01)** — we mark it,
  and the interop suite compares our marker against the reference's byte for
  byte. **The encoding is the part to get right**: a KeyPackage-level component
  `0x0004` with EMPTY data, not a leaf extension and not the obsolete `0x000a`
  extension. Non-empty data there is malformed, so presence is not the test.
- ~~The validator does not verify the KeyPackage or LeafNode signatures~~
  **✅ CLOSED (2026-09-01)** — `v0.1.0-beta.10` added `ValidateKeyPackage`, and
  `KeyPackagePublicationValidator` runs it **before** any Marmot rule, because
  everything after it reads fields off the leaf.

**Publishing landed too** (`ca93ff4`): `KeyPackagePublisher` builds, persists
and publishes, then binds the record to its event id. The ordering is
persist-then-publish and the failure handling is the substance — the relay seam
reports **three** outcomes, and only `Rejected` authorises deleting the record.
A transport that throws is read as `Indeterminate`, because an exception says
nothing about what the relay saw. Erasing the private key for a KeyPackage that
did reach a relay is unrecoverable; accumulating a few dead records is not.
The slot is derived from the newest existing record, so a republish supersedes
rather than accumulates.

**What P6 needs next is in §3i**, which supersedes the list that stood here.

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

### 3i. Create-group is DONE (creator-only) — invite is what remains

`737031c` creates a Current-profile group containing its creator, in
`src/Scramble.Marmot.Engine/Groups/`. **21 tests, mutation-checked.**

**Epoch 0 is the only chance to get the GroupContext right.** Nothing in it can
be repaired without an authorised commit, and an empty admin set cannot be
repaired at all — the commit that would fix it is the one nobody is authorised
to make. Three things go in and all three must be right:

- **`required_capabilities`** — extension `0x0006`, proposal `0x0008`, and
  nothing else. **No component ids appear here.** That is the part that trips
  people: `required_capabilities` is MLS's vocabulary; the required components
  live in the dictionary's own requirement list.
- **The `app_data_dictionary` requirement list** — the negotiated set.
- **State for every component that list names**, except the account-identity
  proof: it is LeafNode-only, and its presence in a GroupContext dictionary is
  an error rather than harmless duplication.

**`RequiredCapabilities` (RFC 9420 §11.1) is implemented on our side.**
`dotnet-mls` references the extension *type* when validating a GroupContext but
never parses its body, so there was nothing to extend. It is generic MLS and a
fair candidate to upstream later; it needs no library change and therefore cost
no permission. It follows producers-canonicalise like the app components, but
for a different reason worth keeping straight: **RFC 9420 states no ordering
requirement**, so rejecting an unsorted list would invent a rule and refuse a
conformant peer — while repairing one would be worse, since a member that
rewrites signed group state holds a canonical form nobody else has.

**Negotiation is built and tested although only the creator is a member.** The
mandatory-component guard is what catches a client whose support set is too
narrow to create a usable group, and finding that at creation beats finding it
when nobody can join. `MandatoryComponents` cannot be negotiated away by an
under-advertising member — such a member is refused instead (mdk#746), because
a group without admin-policy bytes has an empty admin set and frozen
membership, permanently.

**A mutation-testing correction worth reading, because it is the second time
this exact trap has appeared here.** The per-member guard in `Negotiate`
originally *survived* its mutation: the post-condition below it refused the
same inputs, so the guard was doing no work any test could see. It now names
the member at fault — which is the thing a caller can act on, drop that invitee
and retry — and a message naming only the component cannot. Both halves are now
independently load-bearing, each verified by mutating the other away. **The
general rule, already in §4: a guard that survives mutation is not automatically
the guard doing the work.**

**What is NOT here: invitees.** The negotiation and admin-coupling rules
already take member component sets and are ready for the caller that supplies
them. The blocker that kept them out — no way to verify a fetched KeyPackage —
**closed on 2026-09-01** (§3j), so add-members is now just work.

**What P6 needs next is in §3k**, which supersedes the list that stood here.

### 3j. The two open gaps are CLOSED — `v0.1.0-beta.10`

`c21a759` pins the release; `b721255` uses it. Both gaps that earlier sections
documented rather than fixed are now shut, and the interop suite pins each.

**KeyPackage verification.** `dotnet-mls` signed a KeyPackage and a LeafNode
and could verify **neither** — there was no KeyPackageTBS verification anywhere
in it. `MlsGroup.ValidateKeyPackage` now runs the three RFC 9420 §10 checks that
do not depend on the caller's clock or trust model: the LeafNode signature, the
KeyPackage signature, and `init_key != encryption_key`. Lifetime, credential and
capability checks stay with the caller, because the library cannot decide them.

**Why it was not belt-and-braces.** The account-identity proof binds the account
key to the leaf **signature** key and nothing more. Without the KeyPackage
signature, a party could copy a valid leaf *and its proof*, substitute their own
`init_key`, and receive the Welcome — **and the proof would still verify.** The
leaf signature does not cover the `init_key`; only the KeyPackage signature does.
There is an interop test asserting exactly that attack fails.

`KeyPackagePublicationValidator` runs it **before** any Marmot rule. That
ordering is deliberate twice over: everything after it reads fields off the leaf,
and reading them from a KeyPackage whose signature does not verify is reading
attacker-chosen values; and a broken signature should be reported as a malformed
KeyPackage rather than as some Marmot-level mismatch, which would send whoever
reads the error looking in the wrong place.

**Last-resort.** We mark it now, and the interop suite compares our marker
against the reference's **byte for byte**. The encoding is the part to get
right, and two wrong readings are both tempting:

- It is a **KeyPackage-level** component `0x0004` with **EMPTY** data.
- It is **not** a leaf extension — every other dictionary we build is a leaf's,
  which is what makes that reading tempting.
- It is **not** the obsolete `0x000a` extension.
- **Non-empty data there is malformed, not true**, so `IsLastResort` checks
  emptiness rather than presence. A presence check would accept a KeyPackage the
  peer refuses.

Why it matters: kind 30443 is addressable, one live event per slot, so several
people can invite us off one publication. Unmarked, only the first of those
Welcomes could be opened. `KeyPackageRecord.LastResort` is read off the
KeyPackage rather than passed in, so the record and the bytes on the wire cannot
disagree about whether the private material may outlive its first Welcome.

**A note on what an interop test is worth.** The new §10 assertions run against a
*reference-produced* KeyPackage. Our own KeyPackages passing them proves only
that our signer and our verifier agree with each other; a reference KeyPackage
passing them proves the verifier agrees with everyone else. That distinction is
the whole reason this suite exists, and it is worth preserving when adding to it.

**Still open in `dotnet-mls`, unchanged:**

- **(b) SelfRemove — real, ask at P7.** `0x000a` is not expressible; the closed
  `ProposalType` enum stops at `AppDataUpdate = 8`.
- **(d) retained past-epochs — probably avoidable, do not ask yet.** Blocks P8.

### 3k. Invite is DONE — the first two-party group

`d67b699` adds members to a group. **14 tests, mutation-checked**, and one of
them is the first end-to-end exercise in this work: create a group, add a
member, have that member process the Welcome and reach the same epoch with the
same required-component set. That is the KeyPackage builder, the group builder
and the private-material bundle proving they belong to each other.

**Every gate is ours, because the MLS library has almost none.** It defines
`ValidateAddLeafCapabilities` for RFC 9420 §12.1.1 and **never calls it**, and
it has no notion of app components at all. `ValidateInvitee` therefore checks,
in this order:

1. `MlsGroup.ValidateKeyPackage` — **first**, because everything after it reads
   fields off the leaf, and reading them from an unverified KeyPackage is
   reading attacker-chosen values, including the credential returned as a
   member identity.
2. Protocol version and ciphersuite (§12.1.1, the check the library skips).
3. The group's required extension and proposal types.
4. The group's required **app components** — the Marmot half, invisible to MLS.
   A member lacking one joins and then cannot honour state everyone else treats
   as mandatory; the group looks healthy and behaves inconsistently.

**The commit is staged, not applied.** A commit applied locally and never
published forks the committer into an epoch nobody else can reach, and every
message they send afterwards is undecryptable by the group they think they are
in. So: **publish, then apply** — the mirror image of the KeyPackage rule, where
the private material is persisted *before* the publish. **Whichever step is
unrecoverable goes second.** The caller finishes with `Applied()` or
`Discard()`; leaving it unfinished blocks the next commit.

`StagedInvite` is a sealed class, not a positional record, because a record's
primary constructor is public and one built through it would carry no group and
throw from `Applied()` **after the commit was already on a relay**.

**Three test corrections, all found by mutation, and the pattern is the point.**
This is now the third, fourth and fifth time in this work that a test asserted
less than its name claimed:

- The required-proposal test edited a valid leaf. That breaks the leaf
  signature, so it tripped the signature gate and asserted nothing about the
  rule it named. It now builds a correctly signed but non-conformant KeyPackage.
- There is **no** negative test for the required-*extension* gate, because one
  cannot be built: `CreateKeyPackage` unions a carried leaf extension's type
  into the advertised set, and a Marmot leaf always carries the
  `app_data_dictionary`. A test pins that property instead, so the absence is
  explained rather than looking like an oversight.
- "A bad invitee leaves the group untouched" asserted only the epoch — which a
  commit built *before* validation also satisfies, since `CommitPublic` stages
  rather than advances. It now asserts `HasPendingCommit` is false, which is
  what actually distinguishes the two.

**The habit that keeps paying: mutate the code, not just the test.** A green
suite says nothing about which line is load-bearing.

**Not in scope, deliberately: granting admin.** Upstream couples an admin-policy
`AppDataUpdate` into the same commit for invite-with-admin-grant. That needs the
proposal wired through `AppComponentIntegrity`, and doing it badly means an
admin set no member observed being granted.

**What P6 needs next is in §3l**, which supersedes the list that stood here.

### 3l. Welcome publishing is DONE — outbound interop is blocked INSIDE the agent

`ab506e0` adds `WelcomePublication` and the harness the outbound direction
needs. **The engine half is verified (5 unit tests, full round trip). The
end-to-end test is committed skipped**, because it does not pass and the
investigation is worth more than the code.

**What is verified.** A Welcome is serialized as an `MLSMessage` — the receiver
deserializes one and extracts the body, so a bare `Welcome` struct is refused
before anything about the group is read; same rule as the KeyPackage. The rumor
names the KeyPackage **event id**, not the ref, because that is what a recipient
looks their own published KeyPackage up by. A unit test wraps, unwraps as the
recipient, and processes into a joined group at the right epoch. The outer
gift-wrap ephemeral key is generated inside `Wrap` so it cannot be reused —
reuse links every invite a sender makes.

**The harness, which is reusable and was the expensive part:**

- **`socat` is now in the test image.** The agent's control plane is a Unix
  socket and its CLI exposes only `bootstrap`, so before this there was no way
  to ask the agent anything.
- `WnAgentDockerClient.ControlAsync` speaks `marmot.agent-control.v2`.
- `InteropRelayClient.PublishAsync` waits for the relay's `OK` — firing and
  forgetting makes "the relay never took it" look like "the peer has not
  reacted yet" for a whole timeout.

**What the failed runs established, which is the real output of this section:**

1. **Our Welcome is on the relay and well-formed.** Verified directly against
   the relay: kind 1059, correct `p` tag, and a `created_at` inside the NIP-59
   two-day **backwards** jitter window. The publish path is not the problem.
2. **The agent subscribes to nothing on its own.** Its relay connection shows
   `sent: 0 events` in the relay log across 27 hours. `subscribe_inbound` is a
   **streaming** control request — `connection.rs` returns
   `stream_inbound_events` and holds the socket — so the relay subscription
   lives exactly as long as that connection.
3. **`printf | socat` cannot hold it.** stdin hits EOF the moment printf ends,
   socat half-closes, and the subscription is gone before any event arrives.
   The fix is to keep the pipe's writer alive (`{ printf …; sleep N; }`).
4. **A held subscription starves the control pool.** With one open, `group_info`
   returns nothing at all — so the obvious test shape (subscribe, publish, poll)
   cannot work. Read the stream while subscribed; query after releasing.

**What is NOT established, and must not be assumed:** whether holding the
subscription actually makes the agent fetch and process the Welcome. That was
never reached.

**The agent's state does get wedged, and our Welcome is not the cause.** The
first revision of this section wondered whether it was; it is not. What is
established, and the nuance matters because a first correction over-claimed:

- **Two `wn-agent` instances on one SQLite home corrupts it, provably.** A
  `docker run` probe against `scramble_wn-agent-data` while the compose
  container is up will do it. **Never do that** — stop the compose container
  first, or use a separate volume.
- **That is not the only path.** The volume was later left unstartable again
  with only one instance ever running, after a session that held and killed
  `subscribe_inbound` streams. **That trigger was not isolated**, and it should
  not be reported upstream until it is.
- **A clean single-instance run of the whole suite leaves the agent healthy** —
  verified twice, including immediately after this was written.

The symptom sequence is recognisable: `bootstrap` starts timing out (30s) while
`account_list` still answers instantly — that is a wedged account worker — and a
restart from that state fails with `startup failed code=app_error`. **Recovery is
`docker volume rm scramble_wn-agent-data` and a restart**; `bootstrap` rebuilds
the account with a new id, and the suite goes green again.

**Reading the source settled the mechanism.** `subscribe_inbound` →
`stream_inbound_events` → a **one-shot** catch-up at subscription start:
`reconcile()` spawns an account worker per active account, then one `CatchUp`
command goes to each. There is no periodic re-fetch in that loop. So subscribing
first and publishing after leaves the catch-up already run — which is why the
order was inverted. It did not help.

**Everything on our side is now ruled out.** Each of these was checked
directly, not inferred:

- The Welcome **is** on the relay, correct `p` tag, inside the NIP-59
  backwards-jitter window.
- The agent's account is **active and local-signing** (`account.json`), so
  `reconcile()`'s filter admits it.
- The agent publishes **kind-10002 and kind-10050 relay lists, both naming our
  relay** — it knows exactly where to listen.
- **Ordering is not it.** Publishing before and after subscribing were both
  tried.

**Hardcoded relays were the obvious suspect. They are real, and they are not the
cause.** `bootstrap.rs` hardcodes
`DEFAULT_RELAYS = [wss://relay.eu.whitenoise.chat, wss://relay.us.whitenoise.chat]`
and falls back to them. But `--relay` overrides it: it is threaded into
`AgentConnectorConfig.relays` → `MarmotApp::with_relays_…`, and
`ensure_agent_account_relay_lists` seeds the account's lists from it. **Proof it
took effect: the agent publishes kind-10002 and kind-10050 that both name
`ws://127.0.0.1:7777`.** The peer knows exactly where to listen.

**The failure, located: the agent connects to the relay only to PUBLISH.**
Read off the relay's own connection log for a whole day's runs — every
connection the agent opens is `recv: N events, sent: 0 events`, a pure publish.
**Not one connection it makes ever receives an event, and none stays open.** It
never issues a subscription, so the Welcome sits on the relay unread. (An
earlier revision said "zero relay connections"; that was measured in too narrow
a log window and was wrong — it connects, it just never subscribes.)

**The deployed public WhiteNoise runs the Current profile — checked on
2026-09-02, and this answers plan §5's Q1 empirically.** Read-only probe of
`wss://relay.eu.whitenoise.chat`:

- It **rejects kind 443** at the filter (`kind not allowed: 443`), the legacy
  KeyPackage kind. Only `30443` is served.
- Live kind-30443 events there carry **exactly the shape our pinned
  `wn-agent-v0.9.10` publishes** — `mls_extensions` `0x0003,0x0006,0xf2d1,
  0xf2d2,0xf2d4`; `mls_proposals` `0x0008,0x000a`; `app_components`
  `0x8001`–`0x800c`.
- **Our stack validates a real production KeyPackage end to end**: the
  KeyPackageRef we recompute matches theirs byte-for-byte, the lifetime range is
  exactly `7261200`, the last-resort marker reads correctly, the leaf dictionary
  is the expected `{0x0001, 0x0002, 0x8009}`, and the BIP-340 account-identity
  proof verifies.

So the pin is not drifting from production, and the inbound half of our
implementation is confirmed against the real deployment rather than only against
a container. **A real WhiteNoise user is therefore a valid interop peer** — which
matters, because a shipping client subscribes properly and would route around the
`wn-agent` blocker entirely. Two caveats before doing that: it needs a human on
the other end so it cannot be a CI gate, and publishing a KeyPackage under a real
npub to public relays is an outward-facing act that needs a deliberate decision.

**Next step, in order:** raise the relay container's log level to confirm whether
the agent ever sends a `REQ` at all; then the account worker's sync path —
`sync_with_startup_stage_telemetry` in `runtime/account_worker.rs`, which runs
once at worker spawn and is what should be dialing. **Do not re-investigate our
publish side or the relay configuration; both are settled.**

**The process lesson, twice learned now.** Four runs were spent guessing before
reading `stream_inbound_events`, and the reading took twenty minutes and
immediately produced the one-shot-catch-up fact. §3e records the same lesson
about the lifetime blocker. Read the peer first.

### 3m. OUTBOUND INTEROP IS GREEN — and it found a real bug

`fdea0ad`. **The Marmot reference client joins a group Scramble created**,
unattended: it reads our Welcome off the relay, decodes our GroupContext,
accepts, joins, and reports both accounts as members. This is the assertion the
engine existed to reach.

**Use the right peer. There are three and only one works:**

| Peer | What it is | Verdict |
|---|---|---|
| `tests/whitenoise-docker` | `whitenoise-rs` | **Archived upstream** ("this repository is obsolete"), pins `mdk-core 0.8.0`. Legacy protocol only. Keep while `Scramble.Core` ships on it. |
| `tests/wn-agent-docker` | the agent connector | Dark Matter, but a **gateway** — it connects to a relay only to publish and never subscribes, so an invite to it is never read. Fine for KeyPackage-shape checks. |
| `tests/mdk-cli-docker` | **`mdk`'s own `crates/cli`** | **This one.** A full client: groups, invites, messages, sync. |

**Three settings make it work headless**, and each replaces something the old
image needed a source patch for:

- `--secret-store file` — there is no OS keychain in a container.
- `daemon start --discovery-relays / --default-account-relays` — no patching of
  hardcoded relay defaults.
- **`WN_ALLOW_LOOPBACK_RELAYS=1`** — without it *every* relay URL a container can
  be given is rejected as `invalid_relay_url`. There is no CLI flag for this.

Also: the CLI refuses to guess between identities once a peer has more than one
(`multiple accounts exist; pass --account`), so the harness passes `--account` on
every command.

**The bug it found, immediately, which is the whole point of building it.** Our
groups omitted the **Nostr routing component `0x8004`**. The reference client
refused them outright:

> `invalid Nostr routing component: group is missing marmot.transport.nostr.routing.v1`

A peer reads the transport group id and the group's relays out of that component
and **nowhere else**, so a group without it cannot be addressed at all. That is
not a missing feature — it is an unusable group. **Every unit test we had passed
while we emitted them**, because nothing but a real peer can judge what we
produce. Creating a group now requires relays and seeds a routing component with
a random 32-byte `nostr_group_id` — random rather than derived from the MLS group
id, which would let a relay link the two.

**Verified by mutation:** removing `0x8004` again turns the interop test red.

**The lesson, and it is the one this whole phase keeps teaching.** Reading what
we *consume* proves our decoders. Only a peer consuming what we *produce* proves
anything else. Three separate correctness bugs — the unbounded lifetime, the
deferred `0x800c`, and now `0x8004` — were all invisible to a green unit suite.

**What P6 needs next:**

1. **Application messages.** Send a kind-445 the peer can read, and read one it
   sends. That makes it a messenger rather than a key-agreement demo, and the
   peer has `messages send` ready to drive.
2. **Join from a Welcome** — the inbound half.
   `AppComponentIntegrity.ValidateStagedCommit` and `ValidateUpdateBatch` still
   have no caller (§3c); **they must be called as a pair.**
3. **Wire `mdk-cli` into `integration.yml`** so this is a merge gate, not a
   local-only proof.

### 3n. Messages work, both directions, against the reference client

`73b18f0`. **The reference implementation reads what Scramble sends and Scramble
reads what it sends** — plain text, emoji, and bursts. Four interop tests, and
they are real gates: mutating either half of the exporter derivation turns all
four red.

**Why these had to be interop tests.** Joining a group proved the GroupContext
was acceptable and *nothing about messages*. The exporter label, the kind-445
wrap, the MLS application framing and the payload encoding are four separate
agreements. **A wrong exporter context alone would leave both sides silently
unable to read each other while every unit test stayed green.**

**Three layers, each authenticating something different**, and it is worth
keeping them straight:

- **MLS** encrypts the payload and authenticates *which member* sent it.
- **The kind-445 wrap** encrypts the whole MLS message again under
  `MLS-Exporter("marmot", "group-event", 32)` — verified against
  `transport-nostr-peeler`'s own constant — so a relay cannot tell one group's
  traffic from another's. It rotates with the epoch, which is what makes the
  transport layer forward-secret too.
- **The payload names an author**, checked against the MLS sender. This is the
  one with no MLS equivalent and the one that stops a member writing in someone
  else's name. Both send and receive refuse a mismatch, and a test builds the
  forgery by bypassing `Send` to prove the receiver catches it alone.

**The payload format, and its one trap.** It is a Nostr-shaped event carrying
**no signature**, deliberately — MLS already authenticates the sender, so a
signature would only restate it. The **id is not taken on trust**: it is
recomputed as the canonical NIP-01 hash and a mismatch refused, because an id
taken on faith lets a sender label one message as another and every id-keyed
thing downstream — replies, reactions, dedup — then resolves to the wrong event.
**The trap: the payload travels as struct-order JSON (`id, pubkey, created_at,
kind, tags, content`) while its id is the NIP-01 *array* hash.** Confusing the
two produces an event every peer rejects.

**A harness rule, learned the hard way.** All interop classes now share one
xUnit collection. They drive a **single peer container**, and xUnit runs classes
in parallel — two of them creating identities at once corrupted its SQLite
outright (`backend failure: file is not a database`). Any new interop class must
join `DarkMatterInteropCollection`.

**Where interop stands now — 13 passing, 1 skipped:**

| Direction | Status |
|---|---|
| We read their KeyPackage | ✅ byte-exact, incl. a real production one |
| They join a group we create | ✅ |
| They read our messages | ✅ incl. emoji and bursts |
| We read their messages | ✅ with MLS-authenticated sender |
| We join a group *they* create | ❌ not built — the inbound half |

**What P6 needs next:**

1. **Join a group the peer creates.** The last untested direction, and the one
   that exercises `AppComponentIntegrity.ValidateStagedCommit` /
   `ValidateUpdateBatch`, which still have no caller (§3c) — **they must be
   called as a pair.** The peer has `groups create` and can invite us.
2. **Membership changes:** add a third member, remove one, and confirm both
   sides agree on the resulting epoch and member list.
3. **Wire `mdk-cli` into `integration.yml`** so all of this is a merge gate
   rather than a local proof.

### 3o. Join, membership changes, and a second interop bug

`e5060bd`, `eccecdc`. Coverage now spans the message and membership scenarios,
`mdk-cli` is wired into `integration.yml`, and a second real bug turned up.

**The bug, and the lesson attached to it.** Our kind-30443 `app_components` tag
listed `0x0001` and `0x0002`. A peer recomputes that tag from the decoded leaf
**filtered to private-use ids (`>= 0x8000`)** and refuses the whole publication
on any difference — *"app_components tag does not exactly match decoded
KeyPackage metadata"*. The account was therefore unresolvable: no peer would
invite it, and the failure surfaced as an invite that never arrived rather than
as a rejected publish. **§3g had already recorded this exact difference and
dismissed it as "expected and NOT a bug".** It was a bug. Noticing a divergence
from the reference and reasoning it away is not the same as testing it.

**What is covered now** — 823 unit, 13 interop passing:

- **Join from a Welcome** — same group and epoch, the group works afterwards,
  the inviter comes from the verified seal, and the KeyPackage is marked
  consumed only on success. Refusals: a KeyPackage we never published, one whose
  material is erased, and someone else's wrap.
- **Removals** — epoch advances, the member is dropped, no Welcome is produced,
  and the transport key rotates so a removed member cannot peel the outer wrap.
  Refused: removing a non-member, everyone, or nobody.
- **Across epochs** — a conversation survives a member joining; a three-way
  group has every member reading every other with the sender authenticated each
  time.
- **Relay lists** — NIP-65 and NIP-17. **Publishing a KeyPackage is not enough
  to be invitable**; the peer resolves an account from both and refuses to
  invite one it cannot look up.

**The inbound direction is committed skipped, and not because of a defect.**
Discoverability works — the peer resolves us — and it then refuses to create a
group with us because its `groups create` requires **proposal `0x000a`
(SelfRemove) and components `0x8006` (agent-text-stream QUIC) and `0x800b`
(encrypted media v2)**. We advertise none of the three: SelfRemove is not
expressible until `dotnet-mls` grows the type (P7), and the other two are
deferred to P12.

**This is a scoping fact, not a detail.** Those deferrals were made on the
assumption they were optional. They are optional for *creating* groups and
mandatory for *being invited to one* by the reference client. Advertising them
unimplemented would be a lie that lands us in a group whose mandatory state we
then mishandle — exactly what `KnownGroupComponents` exists to prevent. The
requirement is written into the test's skip reason; un-skip when the three
exist.

**What P6 needs next:**

1. **Decide on `0x8006` and `0x800b`.** They are the difference between "a peer
   can join our groups" and "we are a full participant". Either implement them
   or accept that only we can start conversations.
2. **SelfRemove (P7)** — the third of the same set, and the one that needs a
   `dotnet-mls` change.
3. **Convergence (P8)**, once membership churn is real.

### 3p. BOTH DIRECTIONS ARE GREEN — the suite has zero skips

The inbound direction works. The reference client creates a group, invites us,
we join from its Welcome, we read what it sends and it reads what we send back.
`844` unit and `15` interop tests pass, **none skipped**.

Three things had to be true, and §3o predicted only the first two.

**1. SelfRemove (`0x000a`), released as `dotnet-mls v0.1.0-beta.11`.** The
library could not decode the proposal type, so a commit carrying one stranded
every other member at that epoch — a member leaving broke the group for
everybody. Advertising `0x000a` is honest only since that release.

**2. Components `0x8006` and `0x800b`.** Both implemented as policy codecs. The
§3o framing — "either implement them or accept that only we can start
conversations" — was the right call and the answer was implement.

**3. The role capabilities `0xf2d1` / `0xf2d2` / `0xf2d4`, which §3o did not
see coming, and which were nearly got wrong.** Supporting component `0x8006` is
not enough. Its policy carries a `required_member_roles` mask, and each role is
backed by a **separate MLS extension type** checked against every invitee's
KeyPackage — `required_role_capabilities_from_request_components` in
`cgka-engine/src/group_lifecycle.rs`, enforced per member and deliberately *not*
folded into the group's RequiredCapabilities.

`AgentTextStreamPolicy`'s doc comment argued at length that advertising a role
without a QUIC transport would be dishonest, and that carrying the policy alone
was "coherent and safe — we can be in a group that requires no roles of us".
**Both halves were wrong**, and the second is checkable:

- `create_group` in `crates/marmot-app/src/client/mod.rs` pushes `0x8006`
  **unconditionally** — no flag, no config, no CLI option — with
  `user_to_agent_default()`, whose `required_member_roles` is `receive`. There
  is no group from the reference client that requires no role of us. Carrying
  the policy without the roles means never being invited by anyone.
- `leaf_capabilities` in `cgka-engine/src/capabilities.rs` builds the reference
  client's own leaf by walking its **whole** feature registry *"regardless of
  level — that's what 'I support this' means at the leaf"*. All three roles are
  registered, so every conformant client advertises all three. The same binary
  run with `--no-quic` advertises them too.

So a role capability marks understanding, not a live endpoint; availability is
answered separately by `feature_status`. We advertise all three, because we
implement none of them either way — a narrower set would not be more truthful,
only differently arbitrary, and would leave us refused where the reference is
admitted. `KeyPackageInteropTests.TheReferenceAdvertisesAllThreeStreamRolesWith`
`outAQuicTransport` pins that reasoning to the running peer, so a pin bump that
falsifies it fails rather than rotting into a stale comment.

**The generalisable lesson, which is now three-for-three.** Every interop bug
this project has hit was a place where a difference from the reference had been
*noticed* and then reasoned away in prose — the `app_components` tag in §3o, and
now this. Reading upstream is how you form the hypothesis. Running against it is
the only thing that settles it. When a comment explains why a divergence is
fine, that comment is a test that has not been written yet.

**Coverage now, in both directions:** discoverability (NIP-65 + NIP-17 +
KeyPackage), group creation from either side, joining from either side, reading
and sending from a **creator** seat and from an **invitee** seat (different
paths: the invitee's exporter secret, transport id and sender index all come off
the Welcome), multi-message, emoji, three-way groups, removals with transport
key rotation, and cross-epoch conversations.

**What P6 needs next:**

1. **Convergence (P8)** — the natural next step now that membership churn is
   real on both sides.
2. **Keep the suite at zero skips.** `GroupInviteInteropTests` was deleted
   rather than left skipped: it invited `wn-agent`, which never subscribes and
   so could never complete. A permanently skipped test reads as coverage and is
   not.

### 3q. P7 IS DONE — leaving works both ways, and the wire grew a new message

A member can leave a group in either direction against the reference client.
`845` unit, `363` dotnet-mls and `17` interop tests pass, none skipped.

**P7 was the right next step over P8, and not only by size.** We had shipped
SelfRemove into `dotnet-mls` to satisfy a *capability* requirement and never
built the lifecycle that uses it — so we advertised `0x000a` while having no
way to leave or to let anyone else leave. P8 also still depends on the
unresolved past-epoch-secrets question (§4 item (d)).

**The gap nobody had noticed: no handshake message had ever been on the wire.**
Both interop directions bootstrapped a group from a Welcome and then exchanged
application messages. No commit and no proposal had ever crossed between the two
implementations — so our `PublicMessage` framing, our kind-445 wrap of a
handshake, and our reading of one were all completely untested against the
reference despite the suite being green. `GroupHandshake` is that transport, and
`LeaveInteropTests` is the first evidence it agrees with upstream.

**Three `dotnet-mls` gaps, all released.**

1. **`v0.1.0-beta.12` — commit-to-stored-proposals.** The proposal cache was
   write-only from the local group's point of view: `CacheProposal` filled it and
   only an *incoming* commit ever read it. So a peer's proposal could be received,
   verified, and never acted on — and `self_remove` in particular was unusable,
   being illegal inline and inexpressible by anyone but its sender. A member
   could ask to leave and no member could let them. This half of item (b) was
   named in the plan (*"generic store/remove/commit-to-stored-proposals"*) and
   simply not built with the first half.
2. **`v0.1.0-beta.12` — the commit proposal-ordering bug**, found by testing the
   above. `BuildCommit` applied proposals in list order while `ProcessCommitCore`
   applies by type, removes before adds. An add and a removal in one commit
   therefore placed the new leaf in *different slots* on the two sides, and the
   commit was unprocessable by anyone. **This predates commit-by-reference and
   was reachable through the ordinary inline API** — it has its own regression
   test with no references in it, and both ordering tests were confirmed to fail
   against the old order before the fix landed.
3. **`v0.1.0-beta.13` — a member's own removal is reported, not stumbled over.**
   A removed member cannot process the commit that removes them: an UpdatePath
   encrypts path secrets only to remaining members. So the one message that tells
   them they are out is the one they cannot apply. It used to surface several
   steps later as `cannot find decryptable path secret for leaf N` — an ordinary,
   expected event described as though the message were corrupt. Now
   `RemovedFromGroupException`, thrown after the sender's signature verifies and
   before any tree change, so the group is left where it was rather than
   half-advanced.

**A design error worth recording, because the review that caught it was a test
run and not a reading.** `CommitDepartures` refused to commit the last other
member's departure, on the theory that a group of one is degenerate. That
refusal broke *the most common leave there is* — the other person in a
two-person conversation walking away — and it only surfaced because the interop
test used a two-member group. The rule was invented, not derived: nothing in
RFC 9420 or upstream forbids a group of one, the committer always remains so a
commit can never empty the group, and the remaining member can still invite
someone or abandon it. **Guard rules that sound prudent still need a reason that
survives being asked for.**

**What is deliberately not here.** No scheduling and no jitter. Which remaining
member commits a departure, and after how long, needs the clock and a view of
the other members; `MarmotGroupLeave` provides the two mechanisms that policy
composes and nothing above them. The reference client does auto-commit ours —
`TheReferenceClientCommitsOurDeparture` proves it — during sync or maintenance.

**What P6/P7 needs next:** see §3r — items 1 and 2 are now done.

### 3r. The durable leave intent is wired — P7 is complete

`LeaveCoordinator` closes the two gaps §3q left open. 875 unit tests pass.

**The failure it exists to prevent is silent, which is why it needed building
rather than noting.** A `self_remove` proposal is valid only in the epoch it was
framed against, and every member drops its cached proposals on *any* commit —
someone joining, someone else leaving, a routine key rotation. So a departure
request that is overtaken is discarded everywhere with **nobody rejecting it and
nothing reporting a failure**. The member stays in the group; their client shows
them as gone. Nothing about that is visible without the durable record.

So `LeaveRequest` is written **before** the proposal is handed to the caller —
the reverse order loses the intent to a crash between publishing and writing,
which is the exact failure — and `ReproposeIfStaleAsync` re-sends against each
new epoch until a commit resolves it. `ObserveAsync` clears the intent and marks
the group `Removed` on `RemovedByCommit`, for an eviction as much as a
departure: the two reach the same end.

**A real gap the first test run found: a leaver must cache their own proposal.**
The commit that grants a departure cites the proposal **by hash**, so a leaver
who kept no copy cannot resolve what that commit does — the one message telling
them they are out fails with *"unknown proposal reference"* instead of reporting
their removal. `MarmotGroupLeave.Request` now caches it on the local group.
Committing it stays impossible for us and `CommitDepartures` skips it, so the
only thing the copy buys is the ability to understand our own removal.

Worth knowing: **`LeaveInteropTests` passed before this fix**, so the reference
client resolves our SelfRemove into an inline Remove rather than referencing our
proposal. Our own implementation references it, so both shapes now work — but
that difference was invisible until a unit test drove our own commit path.

**The send gate covers two different states and only one is a correctness rule.**
A removed member cannot send anything the group could read; that half is
mechanical. Gating a *pending* departure is a product decision — the send would
work, and a member who has asked to leave has said they are done. **Receiving is
deliberately never gated**: a leaver remains a member until someone commits, and
the commit that finally releases them arrives during exactly that window.

**Still not here, and still deliberate:** no schedule and no jitter. Which
remaining member commits a departure, and when to call `ReproposeIfStaleAsync`,
needs the clock and a view of the other members. The mechanisms are here; the
policy belongs above them.

**What is next:**

1. **P8 convergence** — the remaining phase before hardening, and the
   past-epoch-secrets question (§4 item (d)) has to be settled first: whether
   decrypting from a restored snapshot needs the advanced SecretTree persisted
   back or re-derived per message.
2. **P9 hardening** — session-open hydration would be the natural home for
   calling `ReproposeIfStaleAsync` across every group on start-up.

### 3s. Item (d) is settled, and it found a live bug — read before P8

Full write-up in `scramble-marmot-phased-plan-2026-08.md` §4a. The short form:

**Two application messages from one sender, arriving in the other order, lose
the earlier one permanently.** Not delayed — undecryptable:

```
Generation 0 has already been consumed. Current generation is 2.
```

A Nostr relay is under no obligation to preserve order, so this is reachable in
ordinary use today. It is a **P6 bug**, despite item (d) having been filed as
"blocks P8, not P6".

**Why the suite is green anyway.** Every test we have, unit and interop,
produces and consumes messages in order. `SeveralMessagesAllArrive` polls the
relay and reads them as they come, which on a quiet local relay is creation
order. Nothing has ever delivered message N+1 before message N.

**The mechanism, settled from source.** The plan asked whether to persist an
advanced ratchet back or re-derive per message. **Neither.** OpenMLS's
`DecryptionRatchet` keeps `past_secrets: VecDeque<Option<RatchetKeyMaterial>>`
truncated to `out_of_order_tolerance`, each entry `take`n on use. It retains and
never rewinds, so the snapshot concern in the original note does not arise.

**Three things make ours worse than merely missing.** `Message/SenderRatchet.cs`
implements the tolerance window and **is never constructed** — dead code.
`MlsGroupConfig.OutOfOrderTolerance` and `MaxForwardDistance` are serialised and
read by nothing. So the library advertises a configurable tolerance whose real
value is zero.

**Upstream hit this after our pin.** `DEFAULT_OUT_OF_ORDER_TOLERANCE = 100` and
`DEFAULT_MAXIMUM_FORWARD_DISTANCE = 1000` are new between `v0.9.10` and
`v0.9.17`, reasoned in `wire_format.rs` as *"Marmot transports do not provide
total ordering, so the OpenMLS default of 5 is too small for ordinary relay
reordering and offline catch-up floods."* Match those, plus
`max_past_epochs = 5`.

**Drift is otherwise clean.** `v0.9.10..v0.9.17` over app components,
capabilities, agent-text-stream, engine capabilities and key_package is an
**empty diff** — everything §3g–§3r established still holds at 0.9.17.

**This needs a `dotnet-mls` permission grant** and is a *new* item, not the one
§4 originally wrote. Two parts: within-epoch retention (P6 severity, fixes the
bug above) and across-epoch retention (P8 severity, the `max_past_epochs`
window, which upstream forces to equal the convergence app-message window via
`ensure_app_window_matches` — we must enforce the same equality or diverge
silently on deliverability).

### 3d. Non-code items still open (not blocking)

- **Open a PR for `feat/dark-matter`.** **65 commits** ahead of `master` and
  growing; it is reviewable now and will not be after P6. This is the repo's
  documented flag-day failure mode (I4). *(The user has said a PR is not wanted
  — the plan is to keep going and merge at the end. Recorded here because I4
  names exactly this shape as the risk, not to re-litigate the decision.)*
- ~~**Merge `feat/generic-mls-additions`**~~ **✅ DONE (2026-08-25)** — reviewed,
  merged fast-forward into `main`, released as `v0.1.0-beta.8`.
- ~~**Release the `dotnet-mls` P6 blockers**~~ **✅ DONE (2026-08-31)** — merged
  fast-forward into `main`, released as `v0.1.0-beta.9`, submodule pinned on the
  tag. **`v0.1.0-beta.10` followed on 2026-09-01** with KeyPackage validation
  and KeyPackage-level extensions (§3j).
- ~~**Decide the last-resort question**~~ **✅ DONE (2026-09-01)** — we mark it,
  matching the reference byte for byte (§3j).
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
  is granted, done, and released as **`v0.1.0-beta.10`** (§3e, §3j) —
  **reference the tag, never the branch**, and keep the submodule pinned exactly
  on it. What is
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
| Running a second `wn-agent` against the live compose volume | Two instances on one SQLite home corrupt it | Stop the compose container first, or use a separate volume. |
| `bootstrap` timing out while `account_list` answers instantly | A wedged account worker; a restart from here fails `startup failed code=app_error` | `docker volume rm scramble_wn-agent-data` and restart. Not caused by our Welcome; the trigger is not isolated (§3l). |
| Putting non-private-use ids in the kind-30443 `app_components` tag | A peer recomputes the tag from the leaf filtered to `>= 0x8000` and refuses the publication; the account becomes uninvitable | `0x0001`/`0x0002` belong in the leaf dictionary, never in the tag. |
| Publishing a KeyPackage and expecting to be invitable | The peer resolves an account from its relay lists too and refuses one it cannot look up | Publish kind-10002 and kind-10050 as well. |
| Hashing the struct-order payload JSON to get an app event id | The id is the NIP-01 *array* hash; the JSON is only the transport shape | Two different serialisations. Peers reject a mismatched id. |
| Adding an interop test class without joining `DarkMatterInteropCollection` | xUnit runs classes in parallel against one shared peer and corrupts its SQLite | One collection for all of them. |
| Creating a group without the Nostr routing component `0x8004` | A peer reads the transport group id and relays from it and nowhere else, so the group cannot be addressed; the reference client refuses it | Seed it at creation with a random 32-byte `nostr_group_id`. A green unit suite will not catch this. |
| Reaching for `whitenoise-rs` or `wn-agent` as the interop peer | The first is archived and legacy-only; the second never subscribes, so it cannot receive an invite | Use `tests/mdk-cli-docker` — mdk's own CLI. |
| Expecting `wn-agent` to fetch anything without a held subscription | Its relay connection sits at `sent: 0 events`; `subscribe_inbound` is streaming and the subscription dies with the connection | Hold it open, and keep the pipe's writer alive — `printf \| socat` half-closes at EOF. |
| Polling `group_info` while holding a subscription | A held subscription starves the agent's small control pool and the query returns nothing at all | Read the stream while subscribed; query after releasing. |
| Applying a commit before publishing it | Forks the committer into an epoch nobody else can reach; every message after is undecryptable by the group | Publish, then apply. Whichever step is unrecoverable goes second. |
| Testing a leaf-capability rule by editing a valid leaf | Editing breaks the leaf signature, so the signature gate fires and the rule under test never runs | Build a correctly signed but non-conformant KeyPackage through `CreateKeyPackage`. |
| Asserting "the group was untouched" by epoch alone | `CommitPublic` stages rather than advances, so a commit built before validation leaves the epoch unchanged too | Assert `HasPendingCommit` is false. |
| Reading a last-resort marker by presence | Non-empty data under `0x0004` is malformed, not true — a presence check accepts a KeyPackage the peer refuses | Check the data is EMPTY. And it is a KeyPackage-level component, not a leaf extension and not `0x000a`. |
| Trusting a fetched KeyPackage because its account proof verifies | The proof binds the account key to the leaf SIGNATURE key only; the leaf signature does not cover `init_key` | Run `MlsGroup.ValidateKeyPackage` first. Without it, a copied leaf plus a swapped `init_key` receives the Welcome, proof intact. |
| Putting component ids in `required_capabilities` | It is MLS's vocabulary — extension and proposal types only. Components live in the dictionary's own requirement list | Two different registries, two different places. |
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
