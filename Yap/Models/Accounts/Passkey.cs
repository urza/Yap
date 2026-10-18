namespace Yap.Models.Accounts;

/// <summary>
/// One passkey of an account (AU-27): the public half of a key pair that the person's device made. The private
/// half never leaves the device or its password manager, so this row holds no secret. A sign-in is a signature
/// over a fresh challenge, checked against <see cref="PublicKey"/>.
/// </summary>
public class Passkey
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>The authenticator's own id for the key. A sign-in names the key with it. Unique across the server.</summary>
    public byte[] CredentialId { get; set; } = [];

    /// <summary>The public key in COSE form, as the authenticator handed it over.</summary>
    public byte[] PublicKey { get; set; } = [];

    /// <summary>The authenticator's use counter. Synced passkeys report 0 forever; a counter that goes backwards
    /// means a cloned key, and the library refuses it.</summary>
    public long SignCount { get; set; }

    /// <summary>What the person calls it: "iPhone", "Windows PC (1Password)". Derived from the device at creation
    /// (ST-27) and editable.</summary>
    public string Name { get; set; } = "";

    /// <summary>The domain the key was made for. An authenticator only ever signs for that domain, so a passkey made
    /// on one address of the server is silent on another. Settings says so when the viewer is on a different one.</summary>
    public string RpId { get; set; } = "";

    /// <summary>The authenticator model id. All zeros for most platform passkeys; kept for a future name lookup.</summary>
    public Guid AaGuid { get; set; }

    /// <summary>True when the key is synced through a password manager (iCloud Keychain, Google Password Manager),
    /// so it also lives on the person's other devices.</summary>
    public bool BackedUp { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastUsedAt { get; set; }
}
