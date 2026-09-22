namespace Scramble.Core.Services;

/// <summary>
/// The on-disk framing every <see cref="ISecureStorage"/> implementation shares.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not five private constants any more.</b> Each platform
/// implementation carried its own <c>MagicPrefix</c> — DPAPI on Windows, the
/// Android Keystore, the Apple keychain, libsecret on Linux — and they were
/// equal by coincidence of five people typing the same four bytes. The prefix
/// is not an implementation detail of any one of them: it is how
/// <see cref="ISecureStorage.Unprotect"/> decides whether a value it is handed
/// was ever protected, which is what lets a pre-encryption profile keep opening.
/// A copy that drifted would not fail loudly. It would make that platform read
/// its own protected values as legacy plaintext and hand back ciphertext as
/// though it were data.
/// </para>
/// <para>
/// <b>Never change these bytes.</b> They are a durable format marker: every
/// profile database written by any released build carries them, and a new value
/// makes all of that data unreadable with no error. If a second format is ever
/// needed, the third byte is a version — add a value, do not repurpose one.
/// </para>
/// <para>
/// <b>Test doubles deliberately do not use this.</b> The
/// <c>MockSecureStorage</c> in each test project, and the on-disk assertion in
/// <c>SecureMarmotStorageProviderTests</c>, keep their own literal copies. A
/// test that imported this constant would follow it if it ever changed, and so
/// could not notice the change — which is the one thing those copies exist to
/// do. They are independent pins, not duplication to be tidied away.
/// </para>
/// </remarks>
public static class SecureStorageFormat
{
    /// <summary>
    /// The four bytes prefixed to every protected value.
    /// </summary>
    /// <remarks>
    /// A fresh array per call, so no caller can mutate what the others see.
    /// Implementations copy it into a <c>private static readonly byte[]</c> once
    /// and keep their existing framing code unchanged.
    /// </remarks>
    public static byte[] MagicPrefix => [0xEE, 0xCC, 0x01, 0x00];
}
