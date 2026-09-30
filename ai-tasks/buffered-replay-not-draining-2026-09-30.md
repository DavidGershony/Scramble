# Held messages are never delivered to a member who does not commit

**Status: fixed, 2026-09-30.** Two necessary parts, landed separately:
`35e7217` (drain on somebody else's commit) and the held-envelope store below.
The relay-level reproduction is un-skipped and green.

## The report

A user was invited by a White Noise client into two groups and accepted both.

| Group | Members at join | Members now | Messages sent after joining | Messages visible |
|---|---|---|---|---|
| A | 45 | 49 | yes, confirmed | **none** |
| B | 6 | 6 | yes, confirmed | **none** |

All members are visible in both groups. One relay, operator-controlled.

The control case mattered as much as the failure: a third group that the user
**created from Scramble**, containing two White Noise clients, worked perfectly.
Their own summary was exact, and turned out to be the mechanism:

> if we are not the initiator or the instigator then nothing happens

## The root cause

**A kind-445 envelope sealed under an epoch the device has not yet reached was
dropped with no durable record at all.**

`MarmotSession.ReceiveAsync` peels an envelope against `RetainedTransportKeys`,
which holds the **live** epoch's exporter secret plus the **past** epochs kept in
the archive. A *future* epoch's secret cannot be derived — the commit has to be
applied first, and MLS destroys each epoch's exporter secret on purpose. So the
peel failed, and the method returned `IngestOutcome.TransportDeferred` **without
ever calling ingest** — and ingest is the only thing that writes a
`MessageRecord`.

`TransportDeferred` promises the bytes were kept and will be tried again. On that
path the promise was false. The engine's own documentation admitted it:

> **What is not covered, and cannot be.** An envelope sealed under an epoch we
> have not yet reached opens under no key we hold, and nothing here keeps the
> envelope … That case is left to the relay redelivering.

That is why `35e7217` was necessary and could never be sufficient: **the drain
fired against an empty store.** It is also why the symptom read as "the engine
does not hand the message over" — there was nothing to hand over.

A passive member is behind **by construction**: every other member's commit moves
the group without them. So every message sent at the new epoch hit this path,
which is exactly "if we are not the instigator then nothing happens".

### And the fallback could not save it either

The engine left recovery to "the relay redelivering".
`NostrService.OnNostrEventReceived` drops any event whose id is already in
`_recentlyProcessedEventIds`, **before** routing it — so the one recovery the
engine leaned on was suppressed a layer up. The asymmetry is visible in that
method: the gift-wrap buffer-full path deliberately calls
`_recentlyProcessedEventIds.TryRemove` "so the event can be retried"; the
kind-445 path had no equivalent.

Two independent holes, and together airtight. **Not fixed by touching the dedup
cache** — with the envelope now held durably, redelivery is no longer the
recovery path and the cache is free to do its job.

## What landed

### 1. A durable held-envelope store

- `HeldEnvelope` (`Scramble.Marmot.Abstractions/Storage/`) — keyed by transport
  id, because an envelope that will not peel has no MLS bytes and therefore no
  content-derived `MessageId`. That is the whole reason it cannot be a
  `MessageRecord`.
- Migration `V011`, table `held_envelopes`. `envelope` is a BLOB classified
  `protected`, so it rides the same at-rest path as `messages.wire`; the census
  in `SecureMarmotStorageProviderTests` is updated in the same commit.
- `held_at_epoch` is where **we** stood, deliberately not named `source_epoch`:
  nothing has read the envelope, so there is no sent-at epoch to record.

### 2. `MarmotSession` holds and retries

- `HoldAsync` keeps a retryable peel failure, but only with an **authenticated**
  transport id (`PeelFailedException.TransportId`) and only for an address one of
  our retained epochs actually used.
- `ReplayHeldEnvelopesAsync` runs **before** the message replay, because a held
  envelope can peel into a commit and applying it is what makes the MLS-held
  records readable in the same pass.
- It **sweeps again whenever the epoch moved**. One sweep is not enough: a
  message refused early in a sweep can be readable by the end of it, and a
  passive member has no guaranteed next drain.
- A re-hold keeps the stored row's provenance and bumps only the counters — the
  same shape, and the same reason, as `MessageIngest.PreserveAsync`.

### 3. Bounds, because a routing id is public

Anyone can publish a correctly signed envelope to one of our routing ids that no
key will ever open. Three bounds, each with a test that fails when it is removed:

| Bound | Why |
|---|---|
| `MaxHeldEnvelopesPerGroup = 256`, **keeping the oldest** | Evicting oldest-first would let a flood push out the legitimate message that arrived before it — precisely what an attacker would be buying. |
| Retired past `AppMessagePastEpochLimit` epochs from `held_at_epoch` | Still unopenable that far on means we moved and did not find the key; and the message could not be delivered even if it peeled. |
| Skipped at an epoch already attempted | The key is derived from group state, so asking again where nothing moved asks an answered question. This is what makes a flood free. |

## Tests

`EpochBoundaryPeelTests` is the right home and already covered the *past*-epoch
direction; the future direction had no case at all. Five added, all fast (≈6 s
total, no relay, no Docker):

- `AMessageSealedUnderAnEpochWeHaveNotReachedSurvivesUntilTheCommitArrives` —
  the mirror of the existing `…TheEpochWeJustLeftStillArrives`, and the one that
  reproduced the defect in 585 ms.
- `OneSweepIsNotEnoughWhenAHeldCommitIsWhatUnlocksAHeldMessage`
- `AnEnvelopeStillUnopenableAfterTheWindowIsGivenUpOnRatherThanKept`
- `TheHeldStoreIsCappedAndTheCapKeepsWhatArrivedFirst`
- `AHeldEnvelopeIsNotRetriedAtAnEpochItHasAlreadyFailedAt`

**Each was mutation-tested**: removing the cap, the window retirement, the
same-epoch skip, the re-sweep condition, and the replay call each failed exactly
one test and only that one.

`FullE2EGroupInteropTests.AMemberWhoNeverCommitsStillReceivesWhatAnothersCommitUnlocked`
is un-skipped and green. The line that read

```
After the commit: 0 message(s)      ← the defect
```

now reads `After the commit: 1 message(s)`.

## Why CI never caught it

Every interop test had **Scramble as the instigator**. Two shapes, neither with
another member committing while we sit passive:

- `InboundJoinInteropTests.WeJoinAGroupTheReferenceClientCreated`
- `GroupInteropTests.TheReferenceClientJoinsAGroupScrambleCreated`

The user identified this gap unprompted and it was the most valuable observation
in the investigation. Note the second-order version of it, which is why the
engine-level test above is the load-bearing one: those tests drive the engine
having *already peeled* with the sender's own group, so they step over the
transport layer where this defect lived. A peer-instigated test that still peeled
for itself would have missed it too.

## Candidates from the first investigation, resolved

1. **"The epoch did not really advance far enough."** No — it advanced; there was
   simply nothing stored to replay.
2. **"The replay is keyed on something the drain does not satisfy."** Correct in
   spirit, wrong in location: the missing key was a *store*, not a precondition
   in `MessageIngest.ReplayAsync`.
3. **"The drain runs too early."** No.
4. **`ConvergeAsync` has no app caller.** A real gap, but **not this bug** — the
   inbound commit here applies directly and returns `Processed`, with no
   convergence involved. Still open; see below.

## Still open

- **`ConvergeAsync` has no app caller.** `MessageService` carries a `STILL OPEN`
  note saying a convergence pass adopting a branch is the other event owing a
  replay. Unrelated to this defect, still true.
- **`chat.MlsEpoch` is never updated after an inbound commit.** Stored at join
  and left, so the refusal log prints `epoch=1` for a group that has reached
  epoch 2. Cost real time in this investigation; worth a one-line fix.
- **`HistoryFetchReport` overclaims.** With one address and nothing gained it
  says *"the relay has nothing for this group beyond what you already have"*,
  which it cannot know: messages refused as `Buffered` or `PreMembership` are
  invisible to it, and a held envelope now is too. A test locks the wrong wording
  in (`OneAddressAndNothingNew_BlamesTheRelayNotTheClient`). Counting what was
  discarded and why would have said *"87 messages arrived, all held"* on the
  first press and saved the whole investigation.
- **`HeadlessRealRelayTests` is flaky, pre-existing.** One of its three
  `Category=Integration` tests fails per run and a *different* one each time,
  with "the Welcome names KeyPackage event …, which this device never published".
  Reproduced on a clean tree at `301a54b`, so it is not from this change — the
  three tests appear to consume each other's KeyPackages on the shared compose
  relay. Worth its own ticket.

## What was tried and abandoned

Five attempts at unit coverage from `BufferedReplayWiringTests`. Driving the
inbound path means pushing through the `_events` Subject; the tests passed, then
failed at a 5 s ceiling, then at 30 s, then consistently. The event reaching the
handler is not reliable there and `OnNostrEventReceived` swallows its own
failures, so there is nothing to assert against. Do not retry that route. The
engine-level `MarmotSession` seam turned out to be the right one and is both
faster and sharper — it is where the defect actually was.

## A doc correction this produced

`CLAUDE.md` claimed `FullE2E` is not in the required gate. It is: the Diagnostics
filter is an **include-list** containing `Category=Integration`, and
`FullE2EGroupInteropTests` carries that trait alongside `FullE2E`. Corrected in
`CLAUDE.md`, which warns of exactly this drift.
