using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Yap.Models;

namespace Yap.Services;

/// <summary>
/// Mints and validates the login link tokens that the PWA manifest bakes into its
/// start_url (/pwa-launch?lt=...). An installed PWA — iOS Add-to-Home-Screen especially —
/// launches in an isolated cookie jar, so the browser's auth cookie can't carry over;
/// a token in the URL is the only identity that survives that hand-off.
///
/// Stateless by construction: the token carries userId + expiry, sealed with an HMAC keyed
/// on a server secret AND a hash of the user's current auth token. Binding to the auth
/// token means "sign out other devices" (token rotation) instantly invalidates every
/// outstanding link token for that user — zero revocation bookkeeping.
///
/// Multi-use within the TTL by design: every PWA launch hits the same baked-in start_url,
/// so the token must stay redeemable. After the first redemption the PWA owns a normal
/// auth cookie, so later expiry is harmless.
/// </summary>
public class LinkTokenService
{
    // 72h covers the realistic install→first-launch gap without leaving a
    // months-long bearer URL in the wild.
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(72);

    // Token layout: base64url( userId(16) ∥ expiryUnixSeconds(8) ∥ HMAC-SHA256(32) ) ≈ 75 chars.
    private const int PayloadLength = 24;
    private const int MacLength = 32;
    private const int TokenLength = PayloadLength + MacLength;

    private readonly UserService _userService;
    private readonly ILogger<LinkTokenService> _logger;
    private readonly byte[] _secret;

    public LinkTokenService(UserService userService, IWebHostEnvironment env, ILogger<LinkTokenService> logger)
    {
        _userService = userService;
        _logger = logger;
        _secret = LoadOrCreateSecret(Path.Combine(env.ContentRootPath, "Data", "link-token.key"));
    }

    public string Mint(User user)
    {
        Span<byte> token = stackalloc byte[TokenLength];
        var payload = token[..PayloadLength];
        user.Id.TryWriteBytes(payload[..16]);
        BinaryPrimitives.WriteInt64LittleEndian(payload[16..],
            DateTimeOffset.UtcNow.Add(TokenLifetime).ToUnixTimeSeconds());
        ComputeMac(payload, user.Token, token[PayloadLength..]);
        return Base64Url.EncodeToString(token);
    }

    /// <summary>
    /// Returns the user a well-formed, authentic, unexpired token belongs to; null for
    /// anything else. Deliberately silent about *why* validation failed — /pwa-launch
    /// must not become a login oracle.
    /// </summary>
    public User? Validate(string token)
    {
        Span<byte> bytes = stackalloc byte[TokenLength];
        if (!Base64Url.TryDecodeFromChars(token, bytes, out var written) || written != TokenLength)
            return null;

        var payload = bytes[..PayloadLength];
        var user = _userService.GetById(new Guid(payload[..16]));
        if (user is null)
            return null;

        // The MAC covers the user's *current* auth token, so a rotated token
        // ("sign out other devices") fails right here.
        Span<byte> expectedMac = stackalloc byte[MacLength];
        ComputeMac(payload, user.Token, expectedMac);
        if (!CryptographicOperations.FixedTimeEquals(bytes[PayloadLength..], expectedMac))
            return null;

        var expiry = DateTimeOffset.FromUnixTimeSeconds(BinaryPrimitives.ReadInt64LittleEndian(payload[16..]));
        return expiry >= DateTimeOffset.UtcNow ? user : null;
    }

    private void ComputeMac(ReadOnlySpan<byte> payload, string authToken, Span<byte> destination)
    {
        // mac = HMAC(secret, payload ∥ SHA256(authToken)) — hashing the auth token keeps the
        // MAC input fixed-length and avoids feeding the raw credential into it directly.
        Span<byte> macInput = stackalloc byte[PayloadLength + 32];
        payload.CopyTo(macInput);
        SHA256.HashData(Encoding.UTF8.GetBytes(authToken), macInput[PayloadLength..]);
        HMACSHA256.HashData(_secret, macInput, destination);
    }

    private byte[] LoadOrCreateSecret(string keyPath)
    {
        // Persisted (not in-memory) because the install→first-launch gap can span a server
        // restart or deploy — a fresh secret would strand every already-installed PWA.
        try
        {
            if (File.Exists(keyPath))
            {
                var existing = File.ReadAllBytes(keyPath);
                if (existing.Length == 32)
                    return existing;
                _logger.LogWarning("link-token.key has unexpected length {Length}; regenerating", existing.Length);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not read link-token.key; regenerating");
        }

        var secret = RandomNumberGenerator.GetBytes(32);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(keyPath)!);
            File.WriteAllBytes(keyPath, secret);
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "Could not persist link-token.key — link tokens will not survive a restart");
        }
        return secret;
    }
}
