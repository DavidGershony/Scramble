using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Scramble.Core.Logging;

namespace Scramble.Core.Configuration;

/// <summary>
/// Holds the active profile name and derived paths for multi-instance support.
/// Call <see cref="SetProfile"/> once at startup before any services are created.
/// </summary>
public static class ProfileConfiguration
{
    private static readonly ILogger _logger = LoggingConfiguration.CreateLogger<object>();

    /// <summary>
    /// When true, relay URL validation allows connections to private/reserved IPs (localhost, 10.x, etc.).
    /// Set via --allow-local-relays CLI flag. Default: false (private IPs blocked).
    /// </summary>
    public static bool AllowLocalRelays { get; private set; }

    public static void SetAllowLocalRelays(bool allow) => AllowLocalRelays = allow;

    private static readonly Regex ValidProfileName = new(@"^[a-zA-Z0-9_-]+$", RegexOptions.Compiled);

    /// <summary>
    /// The active profile name (lowercased). "default" when no --profile argument is provided.
    /// </summary>
    public static string ProfileName { get; private set; } = "default";

    /// <summary>
    /// True when a non-default profile is active (i.e. --profile was specified).
    /// </summary>
    public static bool IsCustomProfile { get; private set; }

    /// <summary>
    /// True when the profile was set explicitly via --profile CLI flag.
    /// When false, the profile was auto-derived from the last user's npub.
    /// </summary>
    public static bool WasExplicitlySet { get; private set; }

    /// <summary>
    /// Base data directory for this profile.
    /// Default profile: %LOCALAPPDATA%\Scramble\
    /// Custom profile: %LOCALAPPDATA%\Scramble\profiles\{name}\
    /// </summary>
    public static string DataDirectory { get; private set; } = GetDefaultDataDirectory();

    /// <summary>
    /// Root data directory, independent of active profile. Always %LOCALAPPDATA%\Scramble\.
    /// Used for the user registry file (last_user.json).
    /// Override with <see cref="SetRootDataDirectory"/> in tests to avoid clobbering real user data.
    /// </summary>
    public static string RootDataDirectory { get; private set; } = GetDefaultDataDirectory();

    /// <summary>
    /// Overrides the root data directory. Use in tests to isolate last_user.json
    /// from the real app data directory.
    /// </summary>
    public static void SetRootDataDirectory(string path) => RootDataDirectory = path;

    /// <summary>
    /// Full path to the SQLite database file.
    /// </summary>
    public static string DatabasePath => Path.Combine(DataDirectory, "scramble.db");

    /// <summary>
    /// Directory where log files are written.
    /// </summary>
    public static string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>
    /// Suffix for the window title. Empty for the default profile, " - Alice" for profile "alice".
    /// </summary>
    public static string WindowTitleSuffix { get; private set; } = string.Empty;

    /// <summary>
    /// Sets the active profile. Call once at startup before any services are created.
    /// </summary>
    /// <param name="profileName">Profile name from --profile argument, or null for the default profile.</param>
    /// <param name="explicitOverride">True when set via --profile CLI flag (prevents auto-switching).</param>
    /// <exception cref="ArgumentException">Thrown when the profile name contains invalid characters.</exception>
    public static void SetProfile(string? profileName, bool explicitOverride = false)
    {
        WasExplicitlySet = explicitOverride;

        if (string.IsNullOrWhiteSpace(profileName))
        {
            // Default profile — backward-compatible paths
            ProfileName = "default";
            IsCustomProfile = false;
            DataDirectory = GetDefaultDataDirectory();
            WindowTitleSuffix = string.Empty;
            return;
        }

        profileName = profileName.Trim();

        if (!ValidProfileName.IsMatch(profileName))
        {
            throw new ArgumentException(
                $"Invalid profile name '{profileName}'. Only letters, digits, hyphens, and underscores are allowed.",
                nameof(profileName));
        }

        var lowerName = profileName.ToLowerInvariant();

        ProfileName = lowerName;
        IsCustomProfile = true;
        var root = GetDefaultDataDirectory();
        DataDirectory = Path.Combine(root, "profiles", lowerName);
        // Title-case the first letter for display
        WindowTitleSuffix = " - " + char.ToUpperInvariant(profileName[0]) + profileName[1..].ToLowerInvariant();
    }

    /// <summary>
    /// Derives a deterministic profile name from a Nostr public key hex string.
    /// Uses the first 16 characters (64 bits of entropy — effectively collision-free).
    /// </summary>
    public static string DeriveProfileName(string publicKeyHex)
        => publicKeyHex[..16].ToLowerInvariant();

    /// <summary>
    /// Convenience: sets the profile for a given public key hex (derives the profile name automatically).
    /// </summary>
    public static void SetProfileForAccount(string publicKeyHex)
        => SetProfile(DeriveProfileName(publicKeyHex));

    /// <summary>
    /// Reads the last active user's public key from the registry file (last_user.json).
    /// Returns null if the file doesn't exist or can't be read.
    /// </summary>
    [Obsolete("Use AccountRegistryService.GetActiveAccount() instead. Kept for migration.")]
    public static string? ReadLastUserPubKey()
    {
        try
        {
            var path = Path.Combine(RootDataDirectory, "last_user.json");
            if (!File.Exists(path)) return null;

            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("public_key_hex", out var prop))
            {
                var value = prop.GetString();
                if (!string.IsNullOrEmpty(value) && value.Length == 64)
                    return value;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read last_user.json from {Path}",
                Path.Combine(RootDataDirectory, "last_user.json"));
        }
        return null;
    }

    /// <summary>
    /// Saves the active user's public key to the registry file (last_user.json).
    /// </summary>
    [Obsolete("Use AccountRegistryService.SetActiveAccount() instead. Kept for migration.")]
    public static void WriteLastUserPubKey(string publicKeyHex)
    {
        try
        {
            Directory.CreateDirectory(RootDataDirectory);
            var path = Path.Combine(RootDataDirectory, "last_user.json");
            var json = JsonSerializer.Serialize(new { public_key_hex = publicKeyHex });
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write last_user.json to {Path} (pubkey={PubKey})",
                Path.Combine(RootDataDirectory, "last_user.json"),
                publicKeyHex?[..Math.Min(16, publicKeyHex?.Length ?? 0)] ?? "null");
        }
    }

    /// <summary>
    /// Clears the last user registry. Called on logout so the next launch shows the login screen.
    /// </summary>
    [Obsolete("Use AccountRegistryService.ClearActiveAccount() instead. Kept for migration.")]
    public static void ClearLastUser()
    {
        try
        {
            var path = Path.Combine(RootDataDirectory, "last_user.json");
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear last_user.json");
        }
    }

    private static string GetDefaultDataDirectory()
    {
        var folderName =
#if DEBUG
            "Scramble-Dev";
#else
            "Scramble";
#endif
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            folderName);
    }
}
