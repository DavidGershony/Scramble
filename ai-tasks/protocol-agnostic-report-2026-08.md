# Report — Concord, NIP-29, "Armada-style" multi-protocol, and whether Scramble.Marmot should be protocol-agnostic (2026-08-09)

**Question asked:** should the new `Scramble.Marmot` (Dark Matter) engine be made
protocol-agnostic — able to also speak Concord and/or NIP-29, Armada-style —
**before** the Dark Matter migration finishes?

**Answer up front: No — do not block or reshape the Dark Matter migration for
this.** Agnosticism belongs one layer *above* the engine (a conversation
abstraction in `Scramble.Core`/`Presentation`), and the DM plan already produces
the right seams for free. Adopt one zero-cost discipline now (§6), defer the
rest. Detail below.

**Confidence key:** 🟢 verified against source/spec this session · 🟡 inference.

---

## 1. What the three things actually are

### 1.1 Concord (`github.com/concord-protocol/concord`, CORD-01…08) 🟢

Spec-only repo (52 ⭐, active — updated 2026-08-07; no reference implementation
in the org, just the CORD documents). "Private & decentralised communities on
Nostr" — Discord-shaped: communities, channels, roles, invites, voice.

Architecture (from CORD-01/02, read directly):

- **CORD-01 Private Streams:** a group = one **shared secp256k1 private key**.
  Members publish kind-1059 giftwrap-lookalike events *signed by the shared
  stream key* (fixed author, ephemeral `p` tag — NIP-59 inverted), content
  NIP-44-encrypted under the stream's self-ECDH conversation key. Holding the
  key **is** membership; subscribing is `{"kinds":[1059],"authors":[stream_pk]}`.
- **CORD-02 Communities:** three secrets — `community_id` (self-certifying
  SHA-256 commitment to the owner key), `community_root` (membership key),
  `control_root` (staff-only write key for the control plane). Every
  channel/plane address is HKDF-derived per **epoch**; epochs bump **only on
  removal** (rekey/refounding, CORD-06). Authority = owner-rooted signed roster
  (CORD-04), validated client-side, "enforced by rejection, not by a server."
- **Security model vs Marmot (their own framing, and it's honest):** no
  ratchet, no per-message forward secrecy, no post-compromise security — one
  leaked `community_root` reads the whole epoch. In exchange: no lockstep
  commits, no per-device key packages, no convergence problem — state "folds"
  asynchronously, which is what lets it scale to large high-churn rooms. Their
  README explicitly positions Marmot as the right tool for "small, high-stakes
  groups" and Concord for "the scale and shape of a public community." 🟢

### 1.2 NIP-29 (relay-based groups) 🟢

Groups live **on a relay**; the relay enforces membership and moderation
(kinds 9000-9022 moderation events, relay-signed 39000-39003 metadata, `h` tag
routing, LiveKit AV integration). **No end-to-end encryption** — the relay sees
everything and is the authority. Migration/forking is manual (copy events to
another relay). Trust model is the *opposite* of both Marmot and Concord: you
trust the relay operator.

### 1.3 Armada (soapbox.pub/armada) 🟢

Soapbox's Discord-alternative **client**, and the existence proof for the
pattern the user is asking about: one app speaking **multiple group protocols**
behind one UI — Concord (its primary E2EE standard), **NIP-29**, and Buzz
(Block's team chat), all on one Nostr identity, interoperable with Flotilla,
Vector, Obelisk, etc. The critical observation: **Armada's agnosticism lives
entirely at the client/UI layer.** Nobody makes NIP-29 and Concord share an
engine — they share an identity, a relay pool, and a conversation-list UI. 🟢

---

## 2. What the protocols share — and where

This is the load-bearing table for the whole decision:

| Layer | Marmot/DM | Concord | NIP-29 | Shared? |
|---|---|---|---|---|
| Identity | Nostr keypair | Nostr keypair | Nostr keypair | ✅ **100%** — one nsec, already Scramble's model |
| Relays / subscriptions | kind 444/445/30443 | kind 1059 streams | `h`-tagged kinds + 9000s | ✅ same `NostrService` machinery (subs, NIP-65, backoff, auth) |
| Crypto primitives | NIP-44, NIP-59, ChaCha20, HKDF, schnorr | NIP-44, NIP-59-style wraps, HKDF, schnorr | plain events, NIP-98 HTTP auth | ✅ largely — Concord is *built from* codecs Scramble already owns (`Nip44Encryption`, `GiftWrap`) 🟢 |
| Media | Blossom + MIP-04 | (unspecified/likely Blossom) | plain uploads | ✅ mostly |
| Group state engine | **MLS: epochs, commits, convergence, publish-before-apply** | **shared key + HKDF epochs + folded roster** | **relay-enforced, no client state machine** | ❌ **nothing shared.** Three disjoint models. |
| Membership semantics | leaf in ratchet tree + identity proof | key possession + signed roster | relay's member list | ❌ |
| Moderation/roles | admin app-component | CORD-04 ranked roles | relay policy kinds 9000-9020 | ❌ |

Conclusion from the table: **the reuse surface is the transport/crypto/identity
substrate — which Scramble already owns and which the DM migration already
preserves** (it's the "survives" column of the survives/rewrite diff). The
engines share nothing worth abstracting over. 🟢

---

## 3. Could `Scramble.Marmot` itself be made protocol-agnostic?

Technically yes; it would be a mistake. 🟢 reasoning:

- A common `IGroupEngine` over MLS and Concord is **lowest-common-denominator
  or leaky**. DM's public surface is dominated by concepts with no Concord
  analog: `PendingPublish`/confirm/fail (publish-before-apply), epoch state
  machine, fork recovery, convergence settlement gating sends, KeyPackages,
  staged welcomes. Concord's surface is dominated by concepts with no MLS
  analog: shared-key distribution, control-plane folds, invite bundles,
  refounding. An interface both fit would be `SendMessage`/`OnMessage` — i.e.
  the *conversation* abstraction, which belongs in the app layer anyway.
- **DM already has the only agnosticism that belongs inside the engine:**
  transport-agnosticism via the `TransportPeeler` seam (survives/rewrite diff
  §1, §3 — `Scramble.Marmot.Peeler`). Don't confuse the two: the peeler makes
  Marmot portable across *transports*; it does not and should not make the
  engine generic across *group protocols*.
- Every line of a generic-engine effort is on the critical path of the WN
  deadline, in the repo whose documented failure mode is scope-creep rewrites
  (CLAUDE.md I4; ANALYSIS.md `e05ff875`). The migration is already sized
  Engine **L** + Convergence **L** + AppComponents/AccountProof **M** + 2–4
  dotnet-mls items. Adding "generic over group protocols" to that is the
  classic flag-day trap.

---

## 4. Where agnosticism IS worth having: the Armada-style client seam 🟢

Scramble's architecture already has the right place for it, and it is **not**
inside the engine:

```
Scramble.Presentation (ViewModels)            ← protocol-neutral models ONLY
        │
Scramble.Core services (MessageService, chat list, contacts, profiles)
        │                    │                      │
  Scramble.Marmot      [Scramble.Concord]     [NIP-29 client]     ← engines/providers
        │                    │                      │
   NostrService (relay pool, subs, NIP-65, auth)  ← shared substrate
```

- The DM plan's `Scramble.Marmot` is already self-contained with a narrow
  public API (`SendIntent` / `IngestOutcome` / `GroupEvent` — survives/rewrite
  diff §3). That surface adapts to a future `IConversationProvider` in an
  afternoon; nothing about it needs to change now.
- A future Concord provider slots in **beside** Marmot, not inside it, and
  reuses `NostrService` + the ported `Nip44`/`GiftWrap` codecs. CORD-01 streams
  are, mechanically, "giftwraps with a shared key + HKDF address derivation" —
  a read/write client of CORD-01…05 is plausibly **M**, dwarfed by DM. 🟡
- This is exactly Armada's shape, and it's why they can speak three protocols
  without any of the three engines knowing about the others.

---

## 5. Per-protocol verdict for Scramble

| Protocol | Verdict | Why |
|---|---|---|
| **Marmot/Dark Matter** | **Finish first, unchanged plan** | WN deadline; core product (small/high-security groups); already sized and sequenced. |
| **Concord** | **Attractive later, not now** | Fills Scramble's genuine gap (large communities, channels/roles) with real E2EE and heavy codec overlap with what we own. But: spec-only, no reference impl, weeks old in its current form, evolving — adopting now means chasing *two* pre-1.0 moving specs simultaneously (the exact treadmill the scoping doc warns about, §5). Re-evaluate after DM ships and once Armada/others prove the spec in production. 🟢 |
| **NIP-29** | **Skip as a product surface** | No E2EE + relay-as-authority contradicts Scramble's security posture (the entire sec-critical history, and the fail-closed routing rule). At most a future read-mostly "public community" view for reach. 🟢 |

---

## 6. The recommendation, operationally

**Do not make the engine agnostic. Do these instead:**

1. **Now, zero cost — adopt one discipline for the DM cutover:** no
   `Scramble.Marmot` types cross into `Scramble.Presentation`. ViewModels bind
   only to protocol-neutral models (`Chat`, `Message`, `Member`, `Role`,
   `ChatCapabilities`) surfaced by `Scramble.Core` services, with a
   `protocol` discriminator on the chat record. This is consistent with the
   existing CLAUDE.md layering and is the *entire* prep Armada-style
   multi-protocol needs. Write it into the step-5 phased plan as a cutover
   rule, not a work item.
2. **Now, ~zero cost:** when porting codecs into `Scramble.Marmot.Wire.Nostr`,
   keep `Nip44Encryption`/`GiftWrap` in a namespace not named after Marmot
   (they are generic Nostr crypto, and they're the pieces a Concord provider
   would reuse). One naming decision, no extra code.
3. **Not now:** any `IConversationProvider` interface, any Concord code, any
   NIP-29 code. Interface extraction is cheap *after* there are two concrete
   providers to generalize from and expensive guesswork before.
4. **Post-DM checkpoint:** when Dark Matter is shipped and stable (pivot-freeze
   window passed, I5), re-evaluate Concord: has the spec stabilized? does
   Armada interop exist to test against? If yes, a `Scramble.Concord` provider
   is an additive **M** project sharing the Nostr substrate — no rework of
   Marmot required precisely *because* we kept it out of the engine.

**Why this is the right trade:** the cost of deferring is one interface
extraction later (~days, done against two real implementations instead of one
imagined one). The cost of generalizing now is weeks on the critical path of a
committed deadline, against a moving spec, in the failure mode this repo's own
post-mortem documents. 🟢

---

## Sources

- https://github.com/concord-protocol/concord — README + CORD-01 + CORD-02 (read 2026-08-09)
- https://github.com/nostr-protocol/nips/blob/master/29.md (read 2026-08-09)
- https://soapbox.pub/armada (read 2026-08-09)
- `ai-tasks/survives-rewrite-diff-2026-07.md` §1/§3 (TransportPeeler seam, engine API surface)
- `ai-tasks/dark-matter-migration-scoping-2026-07.md` §5 (parity-chase risk), §10 (layout)
- `CLAUDE.md` invariants I4/I5; `ANALYSIS.md` (flag-day post-mortem)
