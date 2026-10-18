using System.Buffers.Binary;
using System.Buffers.Text;
using System.Formats.Cbor;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Yap.Services.Accounts;

namespace Yap.Tests;

/// <summary>Passkeys (AU-27 to AU-30): a software authenticator registers a key through the Settings routes and
/// signs a new device in through the anonymous routes. The signatures are real; only the device is pretend.</summary>
public class PasskeyTests : IClassFixture<YapFactory>
{
    private readonly YapFactory _app;
    public PasskeyTests(YapFactory app) => _app = app;

    [Fact(DisplayName = "AU-28, AU-29: a registered passkey signs a new device in, once per challenge, until it is removed")]
    public async Task Passkey_registers_and_signs_in()
    {
        var owner = await _app.SignInAsync("pk_owner");
        var authenticator = new FakeAuthenticator();

        // Settings: the two halves of the registration (AU-29)
        var begin = await owner.Http.PostAsync("/api/account/passkeys/options", null);
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        var challenge = await begin.Content.ReadFromJsonAsync<JsonElement>();
        var options = challenge.GetProperty("options");
        Assert.Equal("localhost", options.GetProperty("rp").GetProperty("id").GetString());
        Assert.Equal("required", options.GetProperty("authenticatorSelection").GetProperty("residentKey").GetString());   // discoverable: no username needed (AU-28)

        var registered = await owner.Http.PostAsJsonAsync("/api/account/passkeys", new
        {
            challengeId = challenge.GetProperty("challengeId").GetString(),
            credential = authenticator.Attest(options.GetProperty("challenge").GetString()!, options.GetProperty("user").GetProperty("id").GetString()!),
        });
        Assert.Equal(HttpStatusCode.OK, registered.StatusCode);
        var row = await registered.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Passkey", row.GetProperty("name").GetString());   // no user agent on the test client: the plain default name
        Assert.Equal("localhost", row.GetProperty("rpId").GetString());
        var list = await owner.Http.GetFromJsonAsync<JsonElement>("/api/account/passkeys");
        Assert.Single(list.GetProperty("passkeys").EnumerateArray());

        // A device on another network: the check offers the passkey (AU-08), and the passkey signs in (AU-28)
        var device = _app.CreateClient();
        device.DefaultRequestHeaders.Add("X-Test-Remote-Ip", "203.0.113.9");
        var check = await device.GetFromJsonAsync<JsonElement>("/api/auth/check?username=pk_owner");
        Assert.Equal("verify", check.GetProperty("state").GetString());
        Assert.True(check.GetProperty("passkey").GetBoolean());
        Assert.True(check.GetProperty("code").GetBoolean());   // the login link every account is born with (AU-11)

        var (challengeId, assertion) = await AssertionAsync(device, authenticator);
        var signedIn = await device.PostAsJsonAsync("/api/auth/passkey", new { challengeId, credential = assertion });
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.Contains(signedIn.Headers.GetValues("Set-Cookie"), c => c.StartsWith("yap_auth="));
        Assert.Equal("pk_owner", (await signedIn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("username").GetString());

        // The same answer again: the challenge is spent
        var replay = await device.PostAsJsonAsync("/api/auth/passkey", new { challengeId, credential = assertion });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // Removed in Settings: the key signs nobody in any more (AU-29)
        var id = row.GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.DeleteAsync($"/api/account/passkeys/{id}")).StatusCode);
        var (challengeId2, assertion2) = await AssertionAsync(device, authenticator);
        var afterRemove = await device.PostAsJsonAsync("/api/auth/passkey", new { challengeId = challengeId2, credential = assertion2 });
        Assert.Equal(HttpStatusCode.Unauthorized, afterRemove.StatusCode);
    }

    [Fact(DisplayName = "AU-29: a stranger's passkey cannot be renamed or removed")]
    public async Task Passkeys_are_private()
    {
        var owner = await _app.SignInAsync("pk_private");
        var stranger = await _app.SignInAsync("pk_stranger");
        var authenticator = new FakeAuthenticator();
        var begin = await (await owner.Http.PostAsync("/api/account/passkeys/options", null)).Content.ReadFromJsonAsync<JsonElement>();
        var options = begin.GetProperty("options");
        var registered = await owner.Http.PostAsJsonAsync("/api/account/passkeys", new
        {
            challengeId = begin.GetProperty("challengeId").GetString(),
            credential = authenticator.Attest(options.GetProperty("challenge").GetString()!, options.GetProperty("user").GetProperty("id").GetString()!),
        });
        var id = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.PatchAsJsonAsync($"/api/account/passkeys/{id}", new { name = "mine now" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.DeleteAsync($"/api/account/passkeys/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.Http.PatchAsJsonAsync($"/api/account/passkeys/{id}", new { name = "Work laptop" })).StatusCode);
        var list = await owner.Http.GetFromJsonAsync<JsonElement>("/api/account/passkeys");
        Assert.Equal("Work laptop", list.GetProperty("passkeys")[0].GetProperty("name").GetString());
    }

    [Fact(DisplayName = "AU-29: the default name comes from the device and the password manager")]
    public void Default_name_from_the_device()
    {
        Assert.Equal("iPhone", PasskeyService.DefaultName("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15"));
        Assert.Equal("Windows PC (1Password)", PasskeyService.DefaultName("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/130.0", "1Password"));
        Assert.Equal("Passkey", PasskeyService.DefaultName(null));
    }

    private static async Task<(string ChallengeId, object Credential)> AssertionAsync(HttpClient device, FakeAuthenticator authenticator)
    {
        var begin = await device.PostAsync("/api/auth/passkey/options", null);
        Assert.Equal(HttpStatusCode.OK, begin.StatusCode);
        var challenge = await begin.Content.ReadFromJsonAsync<JsonElement>();
        var options = challenge.GetProperty("options");
        Assert.Equal(0, options.GetProperty("allowCredentials").GetArrayLength());   // the browser picks among the keys it holds (AU-28)
        return (challenge.GetProperty("challengeId").GetString()!, authenticator.Assert(options.GetProperty("challenge").GetString()!));
    }
}

/// <summary>A platform authenticator in a few lines: one P-256 key, a credential id, a use counter. It makes the
/// "none" attestation and the assertions a real device makes, for the test server's origin https://localhost.</summary>
sealed class FakeAuthenticator
{
    private const string Origin = "https://localhost";
    private static readonly byte[] RpIdHash = SHA256.HashData("localhost"u8);

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _credentialId = RandomNumberGenerator.GetBytes(16);
    private string? _userHandle;
    private uint _counter;

    public object Attest(string challenge, string userHandle)
    {
        _userHandle = userHandle;
        var clientData = ClientData("webauthn.create", challenge);
        var authData = AuthData(flags: 0x45, attested: AttestedCredentialData());   // user present, user verified, attested data follows
        var writer = new CborWriter(CborConformanceMode.Ctap2Canonical);
        writer.WriteStartMap(3);
        writer.WriteTextString("fmt"); writer.WriteTextString("none");
        writer.WriteTextString("attStmt"); writer.WriteStartMap(0); writer.WriteEndMap();
        writer.WriteTextString("authData"); writer.WriteByteString(authData);
        writer.WriteEndMap();
        return new
        {
            id = B64(_credentialId), rawId = B64(_credentialId), type = "public-key",
            response = new { attestationObject = B64(writer.Encode()), clientDataJSON = B64(clientData), transports = new[] { "internal" } },
            clientExtensionResults = new { },
        };
    }

    public object Assert(string challenge)
    {
        var clientData = ClientData("webauthn.get", challenge);
        var authData = AuthData(flags: 0x05, attested: null);   // user present, user verified
        var signature = _key.SignData([.. authData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new
        {
            id = B64(_credentialId), rawId = B64(_credentialId), type = "public-key",
            response = new { authenticatorData = B64(authData), clientDataJSON = B64(clientData), signature = B64(signature), userHandle = _userHandle },
            clientExtensionResults = new { },
        };
    }

    private byte[] AuthData(byte flags, byte[]? attested)
    {
        _counter++;
        var counter = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(counter, _counter);
        return [.. RpIdHash, flags, .. counter, .. attested ?? []];
    }

    private byte[] AttestedCredentialData()
    {
        var q = _key.ExportParameters(false).Q;
        var cose = new CborWriter(CborConformanceMode.Ctap2Canonical);
        cose.WriteStartMap(5);
        cose.WriteInt32(1); cose.WriteInt32(2);      // kty: EC2
        cose.WriteInt32(3); cose.WriteInt32(-7);     // alg: ES256
        cose.WriteInt32(-1); cose.WriteInt32(1);     // crv: P-256
        cose.WriteInt32(-2); cose.WriteByteString(q.X!);
        cose.WriteInt32(-3); cose.WriteByteString(q.Y!);
        cose.WriteEndMap();
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)_credentialId.Length);
        return [.. new byte[16], .. length, .. _credentialId, .. cose.Encode()];   // the aaguid is all zeros, like most platform keys
    }

    private static byte[] ClientData(string type, string challenge) =>
        JsonSerializer.SerializeToUtf8Bytes(new { type, challenge, origin = Origin, crossOrigin = false });

    private static string B64(byte[] bytes) => Base64Url.EncodeToString(bytes);
}
