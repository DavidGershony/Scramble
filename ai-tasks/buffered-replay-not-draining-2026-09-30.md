# Held messages are never delivered to a member who does not commit

**Status: unfixed.** One necessary part landed (`35e7217`); it is not sufficient.
A failing reproduction is committed and skipped.

## The report

A user was invited by a White Noise client into two groups and accepted both.

| Group | Members at join | Members now | Messages sent after joining | Messages visible |
|---|---|---|---|---|
| A | 45 | 49 | yes, confirmed | **none** |
| B | 6 | 6 | yes, confirmed | **none** |

All members are visible in both groups. One relay, operator-controlled.

The control case matters as much as the failure: a third group that the **user
created from Scramble**, containing two White Noise clients, works perfectly.
Their own summary was exact:

> if we are not the initiator or the instigator then nothing happens

## What is actually happening

Members being visible rules out most of the obvious theories. Member identities
come out of the `ratchet_tree` extension inside the Welcome, so both Welcomes —
including one for a 45-member group — arrived, fit through the relay, and decoded
completely. **This is not a size problem, and not a Welcome problem.**

An application message that arrives before the device holds the epoch that can
read it comes back `IngestOutcome.Buffered`. Confirmed in the logs, from the
reproduction:

```
12:02:44  expected refusal, group 91f342e5… (epoch=1): deferred — held for replay once the group can read it
12:02:48  processed commit for group 91f342e5…, epoch advanced
12:02:48  expected refusal, event 77517f76… (epoch=1): deferred — held for replay once the group can read it
```

So: the message is held, the commit that should unlock it is processed, the epoch
advances — and it is **still** not delivered. A later message is deferred again.

`HandleGroupMessageEventAsync` swallows all of this deliberately, at
`Information`, via `IsExpectedInboundRefusal`. That is defensible on its own
terms (a healthy client produces these constantly, and surfacing them would
raise a false "group may need reset") but it means the entire failure is
invisible: no banner, no count, no user-visible trace.

## What was fixed, and why it is not enough

`DrainReplayedMessagesAsync` had **seven callers and every one was an operation
we initiate**: `AddMember`, `RemoveMember`, `UpdateAdminPubkeys`,
`PerformSelfUpdate`, `InvitePeerToSyncGroup`, `AddPeerDevice`, and the
merge/rollback helper. The inbound-commit branch advanced the epoch, logged
`"epoch advanced"`, refreshed the admin list, and returned without asking for
anything held.

So a member who never commits anything never drained at all. That is a real gap
and `35e7217` closes it — one call, in that branch.

**It does not fix the reported symptom.** The reproduction still fails with the
drain in place. The drain fires; the engine does not hand the message over.

The `Buffered` arm's own comment claimed the replay "is now actually scheduled:
every `MergeStagedAsync` and every rollback … calls
`DrainReplayedMessagesAsync`". True for our commits, silently untrue for
everyone else's — and, per the above, insufficient either way.

## Where to look next

The question is why `ReplayBufferedMessagesAsync` does not return the held
message once the epoch has advanced. Candidates, none verified:

1. **The epoch did not really advance far enough.** The sender may be further
   ahead than the one commit we processed. In the reproduction there is exactly
   one intervening commit, so this should not apply — but check the engine's
   epoch directly rather than `chat.MlsEpoch` (see the trap below).
2. **The replay is keyed on something the drain does not satisfy** — a horizon,
   an anchor, a pending-commit precondition. `MessageIngest.ReplayAsync` is the
   place to read.
3. **The drain runs too early**, before the engine has committed the epoch
   transition it just applied. Ordering inside the inbound commit path.
4. **`ConvergeAsync` has no app caller at all.** `MessageService` carries a
   `STILL OPEN` note saying a convergence pass adopting a branch is the other
   event owing a replay, and nothing calls it. If the inbound commit is adopted
   through convergence rather than applied directly, nothing replays and nothing
   ever will.

Candidate 4 is the one I would start on.

## Traps found on the way

- **`chat.MlsEpoch` is never updated after an inbound commit.** The refusal log
  prints it, so a group that has advanced to epoch 2 logs `epoch=1`. Anyone
  debugging this will be misled. It is stored at join and left.
- **`ReplayBufferedMessagesAsync` can throw**, and the drain swallows it:
  `"could not replay buffered messages — any message held during this commit
  stays unread until the next one"`. A second silent-loss path.
- **The routing index only goes as deep as our own membership.** The manual
  "Fetch missing messages" button re-asks every address a group has used, but a
  freshly joined member has only ever seen one — so that feature cannot help
  this case, and its report saying "1 address" is how we learned the group had
  not rotated.
- **`HistoryFetchReport` overclaims.** With one address and nothing gained it
  says *"the relay has nothing for this group beyond what you already have"*. It
  cannot know that: messages refused as `Buffered` or `PreMembership` are
  invisible to it. A test locks the wrong wording in
  (`OneAddressAndNothingNew_BlamesTheRelayNotTheClient`). It should count what
  was discarded and why — that would have said *"87 messages arrived, all held"*
  on the first press and saved the whole investigation.

## Why CI never caught it

Every interop test has **Scramble as the instigator**. There are exactly two
shapes, and neither has another member commit while we sit passive:

- `InboundJoinInteropTests.WeJoinAGroupTheReferenceClientCreated`
- `GroupInteropTests.TheReferenceClientJoinsAGroupScrambleCreated`

Worse, those drive the **engine**, while this defect is at the `MessageService`
seam — so even a peer-instigated engine test would have missed it. The user
identified this gap unprompted and it is the most valuable observation in the
whole investigation.

Also: `CLAUDE.md` claims `FullE2E` is not in the required gate. It is. The
Diagnostics filter is an include-list containing `Category=Integration`, and
`FullE2EGroupInteropTests` carries that trait, so it runs. That doc line is
wrong again, as it warns it has been before.

## The reproduction

`FullE2EGroupInteropTests.AMemberWhoNeverCommitsStillReceivesWhatAnothersCommitUnlocked`,
currently `[Fact(Skip = …)]`.

Three Scramble users, no container beyond the relay. Alice creates a group with
Bob; Alice later adds Carol; Alice sends a message at the new epoch; Bob must see
it. **The ordering is forced rather than hoped for** — the bug only bites when
the message is processed before the commit that unlocks it, and a relay serving
oldest-first hands them over in the harmless order. So Bob stays unsubscribed
while both are published, then subscribes with a `since` admitting only the
message, then with one admitting the commit.

Observed, with the fix in place:

```
Bob joined at epoch 1
Alice added Carol (a commit Bob did not make)
After the message alone: 0 message(s) — expected 0, it is held
After the commit: 0 message(s)      ← the defect
```

**Remove the `Skip` as the first step of any fix.** It should go green, and
nothing else in the suite needed to change to observe this.

## What was tried and abandoned

Five attempts at unit coverage from `BufferedReplayWiringTests`. Driving the
inbound path means pushing through the `_events` Subject; the tests passed, then
failed at a 5s ceiling, then at 30s, then consistently. The event reaching the
handler is not reliable there and `OnNostrEventReceived` swallows its own
failures, so there is nothing to assert against. Every stable test in that class
avoids that path. The reason is recorded in the test file. Do not retry this
route — the integration level is the right one, and it works.
