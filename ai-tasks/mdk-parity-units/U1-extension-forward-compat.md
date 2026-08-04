# U1 — Extension forward-compatibility (accept future versions)

**Type:** READY · **Size:** S · **Depends-on:** none · **Do first** (cheap
insurance against a network-wide brick).

## Goal

Guarantee that `NostrGroupDataCodec.Decode` accepts a Marmot Group Data
Extension (`0xF2EE`) whose `version` is **higher than 3** (a future version with
extra trailing fields), by reading the known fields and ignoring unknown trailing
bytes. This matches Rust MDK PR #88 — without it, the moment any peer publishes a
newer-version extension, every group operation would fail.

## Background (all you need)

- File: `lib/marmot-cs/src/MarmotCs.Protocol/Mip01/NostrGroupDataCodec.cs`
- The wire format is: `u16 version`, then `nostr_group_id[32]`, then a series of
  QUIC-varint-length-prefixed opaque fields (name, description, admin_pubkeys,
  relays, image_hash, image_key, image_nonce, image_upload_key, and — for v3 —
  `disappearing_message_secs`).
- Forward-compatibility rule (MIP-01): parse known fields in order, **ignore any
  unknown trailing fields**, reject only `version == 0`.

## Files you may touch

- `lib/marmot-cs/tests/MarmotCs.Protocol.Tests/ProtocolTests.cs` (add a test to
  the existing `Mip01Tests` class)
- `lib/marmot-cs/src/MarmotCs.Protocol/Mip01/NostrGroupDataCodec.cs` — **only if**
  the test below fails.

Touch nothing else.

## Steps (test-first)

1. Read `NostrGroupDataCodec.cs` fully — both `Encode` and `Decode`.
2. Add this test to the `Mip01Tests` class in `ProtocolTests.cs`:

   ```csharp
   [Fact]
   public void NostrGroupDataCodec_FutureVersion_IgnoresUnknownTrailingFields()
   {
       // Build a valid v3 payload, then bump the version to 4 and append an
       // extra opaque<V> "future field". A forward-compatible decoder must read
       // the known v1..v3 fields and ignore the trailing bytes.
       var v3 = new NostrGroupData
       {
           Name = "Future Group",
           Version = 3,
           DisappearingMessageSecs = 3600UL,
       };
       byte[] encoded = NostrGroupDataCodec.Encode(v3);

       // Bump version 3 -> 4 (first two bytes are the big-endian u16 version).
       encoded[0] = 0x00;
       encoded[1] = 0x04;

       // Append a trailing opaque<V> field: 1-byte varint length (3) + 3 bytes.
       var withTrailer = new byte[encoded.Length + 4];
       Array.Copy(encoded, withTrailer, encoded.Length);
       withTrailer[encoded.Length] = 0x03;      // varint length = 3
       withTrailer[encoded.Length + 1] = 0xAA;
       withTrailer[encoded.Length + 2] = 0xBB;
       withTrailer[encoded.Length + 3] = 0xCC;

       var decoded = NostrGroupDataCodec.Decode(withTrailer);

       Assert.Equal(4, decoded.Version);
       Assert.Equal("Future Group", decoded.Name);
       Assert.Equal(3600UL, decoded.DisappearingMessageSecs);
   }
   ```

3. Run it (see verify below).
   - **If it passes:** the decoder is already forward-compatible. Done — the
     value delivered is the regression test. Do **not** change the codec.
   - **If it throws:** the decoder rejects `version > 3` or chokes on trailing
     bytes. Fix `Decode` so it (a) rejects only `version == 0`, (b) reads the
     v3 field when `version >= 3` (already the case), and (c) does not error on
     leftover bytes after the last known field. Keep `Encode` unchanged.

4. Re-run until green, then run the full Protocol suite to confirm no regression.

## Verify (exact commands)

```bash
cd lib/marmot-cs
dotnet test tests/MarmotCs.Protocol.Tests/MarmotCs.Protocol.Tests.csproj \
  -c Debug -p:UseLocalDotnetMls=true \
  --filter "FullyQualifiedName~NostrGroupDataCodec_FutureVersion"
# then the whole protocol suite:
dotnet test tests/MarmotCs.Protocol.Tests/MarmotCs.Protocol.Tests.csproj \
  -c Debug -p:UseLocalDotnetMls=true
```

## Acceptance criteria

- New test passes.
- Full `MarmotCs.Protocol.Tests` suite passes (should be ~255 tests, 0 failed).
- `NostrGroupData` default version is still 2 (do not change it).

## Scope guards

- Do **not** change `Encode`, the default version, or any other codec.
- Do **not** add an upper-version constant/guard — the point is to accept
  unknown-high versions.

## Report back

State: (a) did the test pass without any code change, or did you have to modify
`Decode` (if so, paste the diff); (b) the final test counts from both commands.
Do not commit.
