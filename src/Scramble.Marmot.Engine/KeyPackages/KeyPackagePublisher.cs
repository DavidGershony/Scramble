using DotnetMls.Crypto;
using Scramble.Marmot.Identity;
using Scramble.Marmot.Storage;
using Scramble.Marmot.Wire.Nostr;
using Scramble.Nostr.Crypto;

namespace Scramble.Marmot.Engine.KeyPackages;

/// <summary>
/// What a relay did with a publish attempt.
/// </summary>
/// <remarks>
/// Three outcomes, not two, and the third is the one that matters. Whether the
/// KeyPackage reached the relay decides whether its private material may be
/// destroyed, so "the publish failed" and "the publish may have succeeded" have
/// to be different answers. Collapsing them into a boolean forces the caller to
/// guess, and the wrong guess erases the key material for a KeyPackage other
/// people can already fetch and encrypt Welcomes to.
/// </remarks>
public enum KeyPackagePublishOutcome
{
    /// <summary>The relay accepted the event.</summary>
    Accepted,

    /// <summary>
    /// The relay definitively refused it, and no copy of it exists anywhere.
    /// </summary>
    /// <remarks>
    /// A relay <c>OK: false</c>, or a refusal to even send. Report this only
    /// when the event provably never landed — it is what authorises deleting
    /// the private material.
    /// </remarks>
    Rejected,

    /// <summary>
    /// The attempt neither succeeded nor provably failed.
    /// </summary>
    /// <remarks>
    /// A timeout, a dropped socket, a cancelled send. The event may be live.
    /// The record is left intact so a later reconciliation can settle it
    /// against what the relay actually holds.
    /// </remarks>
    Indeterminate,
}

/// <summary>Publishes a signed event envelope.</summary>
/// <remarks>
/// Deliberately not an abstraction over transports — there is one, and the
/// cutover rules say interfaces get extracted when a second concrete
/// implementation exists, not before. It is an interface only so the publish
/// sequence can be tested without a relay.
/// </remarks>
public interface IKeyPackageRelay
{
    /// <summary>
    /// Publishes <paramref name="envelope"/>.
    /// </summary>
    /// <remarks>
    /// Must not throw for an ordinary failure: return
    /// <see cref="KeyPackagePublishOutcome.Rejected"/> or
    /// <see cref="KeyPackagePublishOutcome.Indeterminate"/> instead. An escaped
    /// exception is treated as indeterminate, which is the safe reading but
    /// loses the distinction the enum exists for.
    /// </remarks>
    Task<KeyPackagePublishOutcome> PublishAsync(string envelope, CancellationToken ct = default);
}

/// <summary>A KeyPackage that reached a relay.</summary>
/// <param name="Bundle">What was built.</param>
/// <param name="SlotId">The publication slot it occupies.</param>
/// <param name="EventIdHex">The kind-30443 event id a Welcome will name it by.</param>
public sealed record PublishedKeyPackage(
    MarmotKeyPackageBundle Bundle, string SlotId, string EventIdHex);

/// <summary>
/// Raised when a KeyPackage could not be published.
/// </summary>
/// <param name="Outcome">
/// What the relay said. <see cref="KeyPackagePublishOutcome.Indeterminate"/>
/// means the record was <b>kept</b>: the KeyPackage may be live.
/// </param>
public sealed class KeyPackagePublishException(
    string message, KeyPackagePublishOutcome outcome, Exception? inner = null)
    : Exception(message, inner)
{
    public KeyPackagePublishOutcome Outcome { get; } = outcome;
}

/// <summary>
/// Builds a KeyPackage, stores it, and publishes it as a kind-30443 event.
/// </summary>
/// <remarks>
/// <para>
/// The ordering is the whole point of this type, and it is not the obvious one.
/// The record and its private material are persisted <b>before</b> anything is
/// sent. Publishing first and persisting after loses the material for a
/// KeyPackage other people can already see, which is unrecoverable: they can
/// encrypt Welcomes to it and we can never open one.
/// </para>
/// <para>
/// The cost of that ordering is an orphan — a record for a KeyPackage that
/// never got published — and cleaning it up is only safe when the publish
/// provably failed. Hence <see cref="KeyPackagePublishOutcome"/>: on
/// <see cref="KeyPackagePublishOutcome.Rejected"/> the record is deleted, and
/// on <see cref="KeyPackagePublishOutcome.Indeterminate"/> it is kept.
/// Accumulating a few dead records is a bounded, fixable problem; erasing the
/// private key for a live KeyPackage is not.
/// </para>
/// <para>
/// <b>The slot is stable across republishes and must stay that way.</b> Kind
/// 30443 is addressable: a relay keeps one event per (author, kind, <c>d</c>
/// tag), so republishing under the same slot supersedes the previous KeyPackage
/// rather than accumulating beside it. A fresh slot each time leaves every old
/// KeyPackage discoverable forever, and every one of them is an invitation we
/// can only honour once.
/// </para>
/// </remarks>
public sealed class KeyPackagePublisher
{
    private readonly ICipherSuite _cs;
    private readonly IAccountIdentityProofSigner _signer;
    private readonly IKeyPackageStorage _storage;
    private readonly IKeyPackageRelay _relay;

    public KeyPackagePublisher(
        ICipherSuite cs,
        IAccountIdentityProofSigner signer,
        IKeyPackageStorage storage,
        IKeyPackageRelay relay)
    {
        ArgumentNullException.ThrowIfNull(cs);
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(relay);

        _cs = cs;
        _signer = signer;
        _storage = storage;
        _relay = relay;
    }

    /// <summary>
    /// The slot this device publishes under, minting one if it has none.
    /// </summary>
    /// <remarks>
    /// Derived from the newest existing record rather than stored on its own.
    /// There is no separate slot table on purpose: a slot with no KeyPackage in
    /// it is not a thing that needs remembering, and two places to record one
    /// value is two places to disagree. Records are returned oldest-first, so
    /// the newest is the last.
    /// </remarks>
    public async Task<string> CurrentSlotIdAsync(CancellationToken ct = default)
    {
        var existing = await _storage.ListKeyPackagesAsync(ct: ct).ConfigureAwait(false);
        return existing.Count > 0 ? existing[^1].SlotId : KeyPackageEvent.NewSlotId();
    }

    /// <summary>
    /// Builds, stores and publishes a KeyPackage.
    /// </summary>
    /// <param name="now">Unix seconds, for the lifetime, proof and event.</param>
    /// <param name="slotId">
    /// The slot to publish under. Defaults to
    /// <see cref="CurrentSlotIdAsync"/>, which is what a republish wants.
    /// </param>
    /// <param name="supportedComponents">See <see cref="MarmotKeyPackageBuilder"/>.</param>
    /// <param name="validitySeconds">See <see cref="KeyPackageLifetimePolicy"/>.</param>
    /// <exception cref="KeyPackagePublishException">The publish did not succeed.</exception>
    public async Task<PublishedKeyPackage> PublishAsync(
        ulong now,
        string? slotId = null,
        IReadOnlySet<ushort>? supportedComponents = null,
        ulong? validitySeconds = null,
        CancellationToken ct = default)
    {
        slotId ??= await CurrentSlotIdAsync(ct).ConfigureAwait(false);

        MarmotKeyPackageBundle bundle = await MarmotKeyPackageBuilder.CreateAsync(
            _cs, _signer, now, supportedComponents, validitySeconds, ct).ConfigureAwait(false);

        string accountHex = Convert.ToHexString(
            _signer.AccountPublicKey.ToArray()).ToLowerInvariant();

        var template = KeyPackageEvent.BuildTemplate(
            accountHex,
            bundle.PublishedBytes,
            slotId,
            bundle.KeyPackageRefHex,
            bundle.CipherSuites,
            bundle.MlsExtensions,
            bundle.MlsProposals,
            bundle.AppComponents,
            createdAt: checked((long)now));

        byte[] id = template.ComputeId();
        byte[] signature = await _signer.SignAsync(template, ct).ConfigureAwait(false);

        // The same rule the proof signer applies, for the same reason: a remote
        // signer can return a signature over a different template or a stale
        // one from an earlier request. Publishing it would burn the slot on an
        // event every relay drops, and the failure would surface far from here.
        if (!AccountIdentityProofSigning.VerifySignedTemplate(
                _signer.AccountPublicKey.Span, template, signature))
        {
            throw new KeyPackagePublishException(
                "The signer returned a signature that does not verify over the KeyPackage event.",
                KeyPackagePublishOutcome.Rejected);
        }

        string eventIdHex = Convert.ToHexString(id).ToLowerInvariant();
        string envelope = NostrEnvelope.Write(template, id, signature);

        // Persisted before a single byte goes out. Everything after this point
        // has to reason about a record that already exists.
        await _storage.PutKeyPackageAsync(
            bundle.ToRecord(slotId, DateTimeOffset.FromUnixTimeSeconds(checked((long)now))), ct)
            .ConfigureAwait(false);

        KeyPackagePublishOutcome outcome;
        Exception? failure = null;
        try
        {
            outcome = await _relay.PublishAsync(envelope, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Indeterminate, not rejected. A transport that throws has told us
            // nothing about whether the relay saw the event, and the safe
            // reading of "nothing" is that it might have.
            outcome = KeyPackagePublishOutcome.Indeterminate;
            failure = ex;
        }

        if (outcome == KeyPackagePublishOutcome.Accepted)
        {
            if (!await _storage.MarkPublishedAsync(bundle.KeyPackageRefHex, eventIdHex, ct)
                    .ConfigureAwait(false))
            {
                // The record we just inserted is gone or has moved on. The
                // KeyPackage is live and we cannot bind it to its event id, so
                // an inbound Welcome naming that id will not find its material.
                throw new KeyPackagePublishException(
                    $"The KeyPackage {bundle.KeyPackageRefHex} was published but could not be " +
                    "marked as such; its record is missing or past Created.",
                    KeyPackagePublishOutcome.Indeterminate);
            }

            return new PublishedKeyPackage(bundle, slotId, eventIdHex);
        }

        if (outcome == KeyPackagePublishOutcome.Rejected)
        {
            // Only here. The relay said no, so nothing anywhere refers to this
            // KeyPackage and its private material has no purpose but risk.
            await _storage.DeleteKeyPackageAsync(bundle.KeyPackageRefHex, ct).ConfigureAwait(false);

            throw new KeyPackagePublishException(
                $"The relay rejected the KeyPackage event {eventIdHex}.",
                KeyPackagePublishOutcome.Rejected);
        }

        throw new KeyPackagePublishException(
            $"The KeyPackage event {eventIdHex} may or may not have been published; " +
            $"the record for {bundle.KeyPackageRefHex} was kept.",
            KeyPackagePublishOutcome.Indeterminate,
            failure);
    }
}
