using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using SimpleFile.PluginHost.Manifest;

namespace SimpleFile.PluginHost.Trust;

public enum SignatureStatus { Unsigned, Verified, Invalid }

public sealed record SignatureState(SignatureStatus Status, string? KeyFingerprint, string? Error);

public static class PluginSignatureVerifier
{
    private static Func<byte[], byte[], byte[], bool> _ed25519Verify = BouncyCastleEd25519;

    /// <summary>
    /// The Ed25519 primitive, as (publicKey, payload, signature). Defaults to BouncyCastle; replaceable
    /// so tests can observe the arguments Verify passes.
    /// </summary>
    public static Func<byte[], byte[], byte[], bool> Ed25519Verify
    {
        get => _ed25519Verify;
        set => _ed25519Verify = value ?? throw new ArgumentNullException(nameof(value));
    }

    private static bool BouncyCastleEd25519(byte[] publicKey, byte[] payload, byte[] signature)
    {
        var signer = new Ed25519Signer();
        signer.Init(false, new Ed25519PublicKeyParameters(publicKey, 0));
        signer.BlockUpdate(payload, 0, payload.Length);
        return signer.VerifySignature(signature);
    }

    public static SignatureState Verify(string folder, PluginManifest manifest)
    {
        try
        {
            var sigPath = Path.Combine(folder, "signature.json");
            if (!File.Exists(sigPath)) return new(SignatureStatus.Unsigned, null, null);

            using var doc = JsonDocument.Parse(File.ReadAllBytes(sigPath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return Invalid("signature.json must be a JSON object.");

            if (!root.TryGetProperty("algorithm", out var alg) || alg.ValueKind != JsonValueKind.String
                || alg.GetString() != "ed25519")
                return Invalid("signature.json algorithm must be \"ed25519\".");

            if (!root.TryGetProperty("files", out var filesEl) || filesEl.ValueKind != JsonValueKind.Object)
                return Invalid("signature.json is missing a \"files\" object.");
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in filesEl.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String)
                    return Invalid($"Hash for \"{p.Name}\" must be a string.");
                files[p.Name] = p.Value.GetString()!;
            }

            if (!root.TryGetProperty("signature", out var sigEl) || sigEl.ValueKind != JsonValueKind.String)
                return Invalid("signature.json is missing a \"signature\" string.");

            var pubKeyB64 = manifest.Publisher.PublicKey;
            if (string.IsNullOrWhiteSpace(pubKeyB64))
                return Invalid("signature.json is present but the manifest has no publisher.publicKey.");

            byte[] pub, sig;
            try { pub = Convert.FromBase64String(pubKeyB64); }
            catch (FormatException) { return Invalid("publisher.publicKey is not valid base64."); }
            if (pub.Length != 32) return Invalid("publisher.publicKey must be exactly 32 bytes.");
            try { sig = Convert.FromBase64String(sigEl.GetString()!); }
            catch (FormatException) { return Invalid("signature is not valid base64."); }
            if (sig.Length != 64) return Invalid("signature must be exactly 64 bytes.");

            var fingerprint = Convert.ToHexString(SHA256.HashData(pub)).ToLowerInvariant()[..16];

            // Recompute every listed file's hash before consulting the primitive.
            foreach (var (path, expected) in files)
            {
                if (!IsBareRelativePath(path)) return Invalid($"Listed path \"{path}\" is not a bare relative path.");
                if (path is "plugin.json" or "signature.json")
                    return Invalid($"\"{path}\" must not be listed in files.");
                var full = Path.Combine(folder, path);
                if (!File.Exists(full)) return Invalid($"Listed file \"{path}\" is missing.");
                var actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(full))).ToLowerInvariant();
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    return Invalid($"Listed file \"{path}\" does not match its recorded hash.");
            }

            var payload = CanonicalPayload(folder, files);
            bool ok;
            try { ok = Ed25519Verify(pub, payload, sig); }
            catch (Exception e)
            {
                // Verification must never throw: any primitive failure means the signature is not valid.
                return new(SignatureStatus.Invalid, fingerprint, "Signature check failed: " + e.Message);
            }
            return ok
                ? new(SignatureStatus.Verified, fingerprint, null)
                : new(SignatureStatus.Invalid, fingerprint, "Signature does not match the package contents.");
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidOperationException)
        {
            return Invalid("Could not verify signature: " + e.Message);
        }
    }

    /// <summary>plugin.json bytes, "\n", then "&lt;path&gt;\n&lt;sha256 hex&gt;\n" per file in ordinal path order.</summary>
    public static byte[] CanonicalPayload(string folder, IReadOnlyDictionary<string, string> files)
    {
        using var ms = new MemoryStream();
        ms.Write(File.ReadAllBytes(Path.Combine(folder, "plugin.json")));
        ms.WriteByte((byte)'\n');
        foreach (var path in files.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            ms.Write(Encoding.UTF8.GetBytes(path + "\n" + files[path] + "\n"));
        }
        return ms.ToArray();
    }

    private static bool IsBareRelativePath(string p) =>
        !string.IsNullOrEmpty(p) && !Path.IsPathRooted(p) && !p.Contains('\\') && !p.StartsWith('/')
        && !p.Split('/').Any(s => s is "" or "." or "..") && p.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    private static SignatureState Invalid(string error) => new(SignatureStatus.Invalid, null, error);
}
