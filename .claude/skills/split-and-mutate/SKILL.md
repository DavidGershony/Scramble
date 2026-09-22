---
name: split-and-mutate
description: Build a component whose correctness is defined by an external spec — protocol rules, a reference implementation, a wire format — by separating the implementer from the test author, then mutation-testing the result. Use when implementing against upstream Marmot/MLS behaviour, when a bug would be invisible until two peers disagree, or when the user asks how to verify tests are load-bearing.
---

# split-and-mutate

For code whose correctness lives outside the repo. The failure mode this exists
for is a rule reproduced *backwards* from a correct reading of upstream: it
looks right, it passes every test its author thinks to write, and it diverges
only on the cases the rule exists for.

Tests written by the implementer cannot catch that, because they encode the same
misreading. Neither can review by the same person, for the same reason.

## When this is worth the cost

It is not free — two passes plus a mutation round. Use it when a mistake would
be **silent**: a consensus rule, a wire format, a codec that a peer validates,
anything where "our tests pass" and "a real peer accepts it" can differ. Do not
use it for ordinary application code where a bug shows up as a visible failure.

## The three roles

Keep them separate even when one person plays all three. The separation is the
mechanism; doing it in one pass gives you nothing.

**1. The brief.** Written by whoever holds the upstream context. It states the
requirement, the facts established by reading upstream (with file and symbol
references so they can be re-checked), and a **fixed API contract** — exact
names, exact exception types, exact substrings in error messages. The contract
is what lets the other two roles proceed without talking to each other.

Include what you are *unsure* of, explicitly. An implementer who hits a wrong
assumption should report it rather than work around it, and can only do that if
it was flagged as an assumption.

**2. The implementer.** Gets the brief. **Writes no tests.** If the contract
looks wrong or impossible, stops and says so rather than adjusting it — a
contract quietly bent to fit an implementation is the whole failure mode
returning by another door.

Run it as a subagent (`Agent`, `general-purpose`, backgrounded) so it starts
without the brief author's assumptions in context.

**3. The test author.** Writes against the **contract**, never the
implementation, and before reading it. Test the properties the spec demands, not
the shape the code happens to have.

## Then mutate — this is the part that pays

Separation alone is not enough. On this repo it has twice produced tests that
looked right and verified nothing. Break the implementation deliberately, one
change at a time, and check the tests notice.

```powershell
Copy-Item src/Path/File.cs $env:TEMP/file.bak
# make ONE targeted change, then:
dotnet test tests/Project/ --filter "FullyQualifiedName~TheseTests"
Copy-Item $env:TEMP/file.bak src/Path/File.cs
```

Pick mutations that a plausible misreading would produce, not random damage:

- **invert a comparison** — the direction of a tie-break, `>` for `>=`
- **off-by-one a bound** — a window, a horizon, a retry count
- **use the live value where a historical one is required** — today's tree
  instead of the epoch's, the current config instead of the stored one
- **delete a guard** — the refusal, not the happy path
- **skip a normalisation** — deduplication, canonical ordering

**A survivor is a finding, and it has two possible causes.** Either the test does
not cover that behaviour, or nothing does and the guard is unreachable. Work out
which. Do not weaken the mutation until something fails.

Some survivors are legitimate: a change with no observable effect through the
public API — memory hygiene, a log line — cannot be caught and should be
recorded as such rather than chased.

## What this does not prove

**The tests still encode the brief author's reading of upstream.** A green run
after all this means *the implementation matches the spec*, not *the spec
matches upstream*. Splitting catches implementation slips. It cannot catch a
specification error, because both halves inherit it.

Only an external oracle closes that gap — a conformance vector, or a live peer.
Check whether one exists before assuming this workflow was sufficient, and check
that the oracle actually distinguishes the case you care about: upstream's
convergence vectors run green against a selector with its tie-break direction
reversed, because both branches in those scenarios tie until that rule and
produce identical observable outcomes either way.

## Worked examples in this repo

| Where | Mutation | Outcome |
|---|---|---|
| `BranchSelection.Compare` | flip `tip_committer` direction | upstream's vectors **survive** — they do not pin it; only our unit tests catch it |
| `MlsGroup` past-epoch window | `MaxPastEpochs + 1` | caught |
| `MlsGroup` past-epoch sender | verify against `_tree` not the retained epoch | **survived** until a leaf-reuse test was written; the original test removed a member whose slot nobody took |
| `MlsGroup` past-epoch guard | allow non-Application content | **survived** — the test used a `PublicMessage` commit, which is never encrypted and never reaches a retained epoch |

The last two were tests that read convincingly and tested nothing.
