#:package WebPush@1.0.13

// Single-file .NET 10 app — generates a VALID VAPID keypair for Yap's push config.
//
//   Run from the repo root:   dotnet run vapidgen.cs
//   Override the subject:     dotnet run vapidgen.cs -- mailto:you@example.com
//
// Requires the .NET 10 SDK (file-based apps). It uses the SAME WebPush library the app
// sends with, so the output format is guaranteed compatible. It also self-verifies that
// the PublicKey is the cryptographic pair of the PrivateKey before printing.

using System.Security.Cryptography;
using WebPush;

var subject = args.Length > 0 ? args[0] : "mailto:you@example.com";

var keys = VapidHelper.GenerateVapidKeys();

// Self-check: sign with the private key, verify with the public key (decoded independently).
// This is exactly the check the push service performs — and exactly what failed before.
bool paired;
try
{
    var pub = FromB64Url(keys.PublicKey);   // 0x04 || X(32) || Y(32)
    var d   = FromB64Url(keys.PrivateKey);  // 32-byte scalar
    using var signer = ECDsa.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256,
        D = d,
        Q = new ECPoint { X = pub[1..33], Y = pub[33..65] },
    });
    using var verifier = ECDsa.Create(new ECParameters
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = pub[1..33], Y = pub[33..65] },
    });
    var probe = "yap-vapid-keypair-self-test"u8.ToArray();
    var sig = signer.SignData(probe, HashAlgorithmName.SHA256);
    paired = verifier.VerifyData(probe, sig, HashAlgorithmName.SHA256);
}
catch (Exception ex)
{
    paired = false;
    Console.Error.WriteLine($"Self-test error: {ex.Message}");
}

Console.WriteLine();
Console.WriteLine(paired
    ? "✅ Verified: PublicKey IS the cryptographic pair of PrivateKey."
    : "❌ Self-test FAILED — do NOT use these keys.");
Console.WriteLine();

Console.WriteLine("── Paste into Data/appsettings.json ──────────────────────────────");
Console.WriteLine($$"""
  "Vapid": {
    "Subject": "{{subject}}",
    "PublicKey": "{{keys.PublicKey}}",
    "PrivateKey": "{{keys.PrivateKey}}"
  },
""");
Console.WriteLine("── Or as environment variables (override the JSON) ───────────────");
Console.WriteLine($"Vapid__Subject={subject}");
Console.WriteLine($"Vapid__PublicKey={keys.PublicKey}");
Console.WriteLine($"Vapid__PrivateKey={keys.PrivateKey}");
Console.WriteLine();
Console.WriteLine("Keep PrivateKey secret. After deploying, existing subscribers must");
Console.WriteLine("re-subscribe — their old subscription is bound to the previous key.");

static byte[] FromB64Url(string s)
{
    s = s.Replace('-', '+').Replace('_', '/');
    s = (s.Length % 4) switch { 2 => s + "==", 3 => s + "=", _ => s };
    return Convert.FromBase64String(s);
}
