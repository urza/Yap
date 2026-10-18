using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.EntityFrameworkCore;
using Yap.Configuration;
using Yap.Models.Accounts;
using Yap.Services.Persistence;

namespace Yap.Services.Accounts;

/// <summary>A ceremony that cannot go on: an expired challenge, an unknown key, a signature that does not check out.
/// The message is for the person; the endpoint turns it into a 4xx.</summary>
public sealed class PasskeyException(string message) : Exception(message);

/// <summary>The first half of a ceremony: the options the browser needs, and the id this server remembers them under.</summary>
public sealed record PasskeyChallenge(string ChallengeId, JsonElement Options);

/// <summary>
/// Passkeys (AU-27 to AU-30): the WebAuthn ceremonies and every account's credentials, memory first like the rest of
/// the data layer. A ceremony has two halves. <c>Begin*</c> hands the browser a challenge and remembers it;
/// <c>Complete*</c> checks the browser's answer against that challenge and forgets it, so an answer is good once.
/// The challenges live in memory only: a restart between the halves fails the ceremony, and the person taps again.
/// The cryptography is Fido2NetLib's; this class only holds the state around it.
/// </summary>
public sealed class PasskeyService
{
    // Passkey autofill leaves a sign-in challenge parked in the username box until the person picks a key, so the
    // life is generous. A challenge is 16 random bytes that one signature consumes, so age costs nothing.
    private static readonly TimeSpan ChallengeLife = TimeSpan.FromMinutes(10);
    private const int MaxPendingChallenges = 5000;   // the sign-in options route is anonymous; this bounds what a flood can park here
    private const int MaxNameLength = 64;

    private readonly IDbContextFactory<ChatDbContext>? _dbFactory;   // startup load only
    private readonly PersistenceWriter _writer;
    private readonly ILogger<PasskeyService> _logger;
    private readonly string _serverName;

    private readonly ConcurrentDictionary<Guid, Passkey> _passkeys = new();
    private readonly ConcurrentDictionary<string, Guid> _byCredential = new();   // base64url credential id -> row
    private readonly ConcurrentDictionary<string, Pending> _pending = new();     // challenge id -> what the browser got
    private readonly object _lock = new();

    /// <summary>The options as JSON, because Fido2NetLib compares the answer against its own serialized form. UserId is
    /// the account a registration was started for, so one person's challenge cannot finish another person's ceremony.</summary>
    private sealed record Pending(string OptionsJson, Guid? UserId, DateTime ExpiresAt);

    public PasskeyService(IServiceProvider serviceProvider, PersistenceWriter writer, ServerConfig config, ILogger<PasskeyService> logger)
    {
        _dbFactory = serviceProvider.GetService<IDbContextFactory<ChatDbContext>>();
        _writer = writer;
        _logger = logger;
        _serverName = config.ProjectName;
    }

    /// <summary>Loads every passkey from the database. Called once at startup, after users.</summary>
    public async Task LoadAsync()
    {
        if (_dbFactory is null) return;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var rows = await db.Passkeys.AsNoTracking().ToListAsync();
            foreach (var passkey in rows)
            {
                _passkeys[passkey.Id] = passkey;
                _byCredential[Key(passkey.CredentialId)] = passkey.Id;
            }
            _logger.LogInformation("Loaded {Count} passkeys", rows.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load passkeys");
        }
    }

    // ---- lookup ----------------------------------------------------------------------------------------------

    public IReadOnlyList<Passkey> ForUser(Guid userId) => _passkeys.Values.Where(p => p.UserId == userId).OrderBy(p => p.CreatedAt).ToList();

    public bool HasAny(Guid userId) => _passkeys.Values.Any(p => p.UserId == userId);

    // ---- registration (AU-29) --------------------------------------------------------------------------------

    public PasskeyChallenge BeginRegistration(User user, HostString host)
    {
        var options = Fido(host).RequestNewCredential(new RequestNewCredentialParams
        {
            User = new Fido2User { Id = user.Id.ToByteArray(), Name = user.Username, DisplayName = user.EffectiveDisplayName },
            // The keys the account has already: the authenticator that holds one of them refuses to make a second.
            ExcludeCredentials = ForUser(user.Id).Select(p => new PublicKeyCredentialDescriptor(p.CredentialId)).ToList(),
            // Discoverable, so the login page can offer the key without a username (AU-28). Verification "preferred":
            // phones and laptops ask for the face, the finger or the PIN anyway, and a plain security key still works.
            AuthenticatorSelection = new AuthenticatorSelection { ResidentKey = ResidentKeyRequirement.Required, UserVerification = UserVerificationRequirement.Preferred },
            AttestationPreference = AttestationConveyancePreference.None,   // which make of device holds the key is nobody's business here
            Extensions = new AuthenticationExtensionsClientInputs { CredProps = true },   // the browser may name the password manager (DefaultName)
        });
        return Remember(options.ToJson(), user.Id);
    }

    /// <summary>Checks the browser's new credential against the challenge and keeps it. Throws <see cref="PasskeyException"/>
    /// when the answer does not fit.</summary>
    public async Task<Passkey> CompleteRegistrationAsync(User user, string? challengeId, JsonElement credential, HostString host, string? userAgent, CancellationToken ct)
    {
        var pending = Take(challengeId, user.Id) ?? throw new PasskeyException("The passkey request expired. Try again.");
        var response = Parse<AuthenticatorAttestationRawResponse>(credential);
        RegisteredPublicKeyCredential made;
        try
        {
            made = await Fido(host).MakeNewCredentialAsync(new MakeNewCredentialParams
            {
                AttestationResponse = response,
                OriginalOptions = CredentialCreateOptions.FromJson(pending.OptionsJson),
                IsCredentialIdUniqueToUserCallback = (args, _) => Task.FromResult(!_byCredential.ContainsKey(Key(args.CredentialId))),
            }, ct);
        }
        catch (Fido2VerificationException ex)
        {
            _logger.LogInformation("Passkey registration refused for {Username}: {Code} {Message}", user.Username, ex.Code, ex.Message);
            throw new PasskeyException("This passkey could not be verified. Try again.");
        }

        var passkey = new Passkey
        {
            UserId = user.Id,
            CredentialId = made.Id,
            PublicKey = made.PublicKey,
            SignCount = made.SignCount,
            Name = DefaultName(userAgent, response.ClientExtensionResults?.CredProps?.AuthenticatorDisplayName),
            RpId = host.Host,
            AaGuid = made.AaGuid,
            BackedUp = made.IsBackedUp,
        };
        lock (_lock)
        {
            _passkeys[passkey.Id] = passkey;
            _byCredential[Key(passkey.CredentialId)] = passkey.Id;
        }
        _writer.Enqueue(Writes.Passkey(passkey));
        return passkey;
    }

    // ---- sign-in (AU-28) -------------------------------------------------------------------------------------

    public PasskeyChallenge BeginSignIn(HostString host)
    {
        var options = Fido(host).GetAssertionOptions(new GetAssertionOptionsParams
        {
            AllowedCredentials = [],   // none named: the browser offers every passkey it holds for this domain, so no username is typed
            UserVerification = UserVerificationRequirement.Preferred,
        });
        return Remember(options.ToJson(), null);
    }

    /// <summary>The passkey that signed the challenge. Throws <see cref="PasskeyException"/> when the answer does not fit;
    /// the caller decides what a failed sign-in logs.</summary>
    public async Task<Passkey> CompleteSignInAsync(string? challengeId, JsonElement credential, HostString host, CancellationToken ct)
    {
        var pending = Take(challengeId, null) ?? throw new PasskeyException("The sign-in request expired. Tap your passkey again.");
        var response = Parse<AuthenticatorAssertionRawResponse>(credential);
        if (response.RawId is not { Length: > 0 } || !_byCredential.TryGetValue(Key(response.RawId), out var id) || !_passkeys.TryGetValue(id, out var passkey))
            throw new PasskeyException("This passkey is not registered here. It may have been removed in Settings.");

        VerifyAssertionResult result;
        try
        {
            result = await Fido(host).MakeAssertionAsync(new MakeAssertionParams
            {
                AssertionResponse = response,
                OriginalOptions = AssertionOptions.FromJson(pending.OptionsJson),
                StoredPublicKey = passkey.PublicKey,
                StoredSignatureCounter = (uint)passkey.SignCount,
                IsUserHandleOwnerOfCredentialIdCallback = (args, _) => Task.FromResult(args.UserHandle.AsSpan().SequenceEqual(passkey.UserId.ToByteArray())),
            }, ct);
        }
        catch (Fido2VerificationException ex)
        {
            _logger.LogInformation("Passkey sign-in refused for passkey {Id}: {Code} {Message}", passkey.Id, ex.Code, ex.Message);
            throw new PasskeyException("This passkey could not be verified.");
        }

        passkey.SignCount = result.SignCount;
        passkey.BackedUp = result.IsBackedUp;
        passkey.LastUsedAt = DateTime.UtcNow;
        _writer.Enqueue(Writes.Passkey(passkey));
        return passkey;
    }

    // ---- the person's list (ST-27) ---------------------------------------------------------------------------

    /// <summary>Renames the person's own passkey. False when the id is not theirs.</summary>
    public bool Rename(Guid userId, Guid id, string? name)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > MaxNameLength) return false;
        if (!_passkeys.TryGetValue(id, out var passkey) || passkey.UserId != userId) return false;
        passkey.Name = trimmed;
        _writer.Enqueue(Writes.Passkey(passkey));
        return true;
    }

    /// <summary>Removes the person's own passkey. The key itself stays on their device until they delete it there;
    /// it just signs nobody in any more. False when the id is not theirs.</summary>
    public bool Remove(Guid userId, Guid id)
    {
        lock (_lock)
        {
            if (!_passkeys.TryGetValue(id, out var passkey) || passkey.UserId != userId) return false;
            _passkeys.TryRemove(id, out _);
            _byCredential.TryRemove(Key(passkey.CredentialId), out _);
        }
        _writer.Enqueue(Writes.DeletePasskey(id));
        return true;
    }

    /// <summary>Account deletion: the memory side. The database cascades the rows.</summary>
    public void ForgetUser(Guid userId)
    {
        lock (_lock)
        {
            foreach (var passkey in _passkeys.Values.Where(p => p.UserId == userId).ToList())
            {
                _passkeys.TryRemove(passkey.Id, out _);
                _byCredential.TryRemove(Key(passkey.CredentialId), out _);
            }
        }
    }

    // ---- inside ----------------------------------------------------------------------------------------------

    /// <summary>The relying party as the browser sees it: the host of this request. The authenticator binds a key to
    /// that domain and signs the domain's hash into every answer, so a request with the wrong host can only fail
    /// the check, never pass it. Both schemes are listed because the browser does WebAuthn on https or on
    /// localhost only; the http entry serves local development.</summary>
    private Fido2 Fido(HostString host) => new(new Fido2Configuration
    {
        RPID = host.Host,
        RPName = _serverName,
        Origins = new HashSet<string> { $"https://{host.Value}", $"http://{host.Value}" },
    });

    private PasskeyChallenge Remember(string optionsJson, Guid? userId)
    {
        var now = DateTime.UtcNow;
        foreach (var (id, pending) in _pending) if (pending.ExpiresAt <= now) _pending.TryRemove(id, out _);
        if (_pending.Count >= MaxPendingChallenges) throw new PasskeyException("Too many sign-in requests right now. Try again in a minute.");
        var challengeId = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        _pending[challengeId] = new Pending(optionsJson, userId, now + ChallengeLife);
        return new PasskeyChallenge(challengeId, JsonSerializer.Deserialize<JsonElement>(optionsJson));
    }

    /// <summary>The remembered options, once. A second answer to the same challenge finds nothing.</summary>
    private Pending? Take(string? challengeId, Guid? userId)
    {
        if (challengeId is null || !_pending.TryRemove(challengeId, out var pending)) return null;
        return pending.ExpiresAt > DateTime.UtcNow && pending.UserId == userId ? pending : null;
    }

    private static T Parse<T>(JsonElement credential)
    {
        if (credential.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) throw new PasskeyException("The passkey answer is empty.");   // a body without `credential`
        try { return JsonSerializer.Deserialize<T>(credential.GetRawText()) ?? throw new PasskeyException("The passkey answer is empty."); }
        catch (JsonException) { throw new PasskeyException("The passkey answer could not be read."); }
    }

    private static string Key(byte[] credentialId) => Base64Url.EncodeToString(credentialId);

    /// <summary>A name the person recognises in the list: the device, plus the password manager when the browser names
    /// it ("iPhone (iCloud Keychain)"). A hex id would tell them nothing. They can rename it.</summary>
    internal static string DefaultName(string? userAgent, string? provider = null)
    {
        var ua = userAgent ?? "";
        var device = ua switch
        {
            _ when ua.Contains("iPhone") => "iPhone",
            _ when ua.Contains("iPad") => "iPad",
            _ when ua.Contains("Android") => "Android",
            _ when ua.Contains("CrOS") => "Chromebook",
            _ when ua.Contains("Macintosh") => "Mac",
            _ when ua.Contains("Windows") => "Windows PC",
            _ when ua.Contains("Linux") => "Linux",
            _ => "Passkey",
        };
        var name = string.IsNullOrWhiteSpace(provider) ? device : $"{device} ({provider.Trim()})";
        return name.Length <= MaxNameLength ? name : device;
    }
}
