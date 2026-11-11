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
    /// <summary>The largest <c>signature.json</c> the host reads, 1 MiB, the <c>plugin.json</c> cap.</summary>
    public const int MaxSignatureBytes = PluginManifestReader.MaxManifestBytes;

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

    public static SignatureState Verify(string folder, PluginManifest manifest) => Verify(folder, manifest, null);

    /// <summary>
    /// Checks <c>signature.json</c> in <paramref name="folder"/> against <paramref name="manifestBytes"/>, the
    /// plugin.json bytes discovery read, parsed and hashed; null reads <c>plugin.json</c> from the folder.
    /// </summary>
    public static SignatureState Verify(string folder, PluginManifest manifest, byte[]? manifestBytes)
    {
        try
        {
            var sigPath = Path.Combine(folder, "signature.json");
            if (!File.Exists(sigPath)) return new(SignatureStatus.Unsigned, null, null);

            byte[] sigBytes;
            using (var sigStream = new FileStream(sigPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 1, FileOptions.SequentialScan))
            {
                if (!PluginManifestReader.TryReadBounded(sigStream, MaxSignatureBytes, out sigBytes))
                    return Invalid("signature.json is larger than 1 MiB.");
            }

            // The signing tool accepts a leading UTF-8 byte order mark, so the host does too.
            var json = sigBytes.AsMemory();
            if (json.Span.StartsWith(Encoding.UTF8.Preamble)) json = json[Encoding.UTF8.Preamble.Length..];
            using var doc = JsonDocument.Parse(json);
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

            // "Verified" must speak for the code that runs: the declared UI assembly and backend executable
            // have to be covered by the signature, or a swapped binary would keep the Verified status.
            foreach (var code in new[] { manifest.Ui?.Assembly, manifest.Backend?.Executable })
            {
                if (string.IsNullOrEmpty(code) || !File.Exists(Path.Combine(folder, code))) continue;
                if (!files.Keys.Any(k => string.Equals(k, code, StringComparison.OrdinalIgnoreCase)))
                    return Invalid($"\"{code}\" is declared by the manifest but not listed in signature.json files.");
            }

            var fingerprint = Convert.ToHexString(SHA256.HashData(pub)).ToLowerInvariant()[..16];

            // Recompute every listed file's hash before consulting the primitive.
            foreach (var (path, expected) in files)
            {
                if (!IsBareRelativePath(path)) return Invalid($"Listed path \"{path}\" is not a bare relative path.");
                if (path is "plugin.json" or "signature.json")
                    return Invalid($"\"{path}\" must not be listed in files.");
                var full = Path.Combine(folder, path);
                if (!File.Exists(full)) return Invalid($"Listed file \"{path}\" is missing.");
                string actual;
                using (var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan))
                {
                    actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                }
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                    return Invalid($"Listed file \"{path}\" does not match its recorded hash.");
            }

            var payload = manifestBytes is null ? CanonicalPayload(folder, files) : CanonicalPayload(manifestBytes, files);
            bool ok;
            try { ok = Ed25519Verify(pub, payload, sig); }
            catch (Exception e)
            {
                // Verification must never throw: any primitive failure means the signature is not valid.
                return new(SignatureStatus.Invalid, fingerprint, "Signature check failed: " + e.Message);
            }
            if (!ok) return new(SignatureStatus.Invalid, fingerprint, "Signature does not match the package contents.");

            // "Signed" must speak for every file the plugin can load, not only the ones the publisher chose to list:
            // an unlisted file beside the signed ones (a planted version.dll, a second assembly) makes the state Invalid.
            var unlisted = Unlisted(folder, files.Keys);
            return unlisted.Count switch
            {
                0 => new(SignatureStatus.Verified, fingerprint, null),
                1 => new(SignatureStatus.Invalid, fingerprint,
                    $"\"{unlisted[0]}\" is in the plugin folder but not listed in signature.json files."),
                _ => new(SignatureStatus.Invalid, fingerprint,
                    $"\"{unlisted[0]}\" and {unlisted.Count - 1} other files in the plugin folder are not listed in signature.json files."),
            };
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException
                                      or ArgumentException or InvalidOperationException)
        {
            return Invalid("Could not verify signature: " + e.Message);
        }
    }

    /// <summary>plugin.json bytes, "\n", then "&lt;path&gt;\n&lt;sha256 hex&gt;\n" per file in ordinal path order.</summary>
    public static byte[] CanonicalPayload(string folder, IReadOnlyDictionary<string, string> files) =>
        CanonicalPayload(File.ReadAllBytes(Path.Combine(folder, "plugin.json")), files);

    /// <summary>The <see cref="CanonicalPayload(string, IReadOnlyDictionary{string, string})"/> of these plugin.json bytes.</summary>
    public static byte[] CanonicalPayload(byte[] manifestBytes, IReadOnlyDictionary<string, string> files)
    {
        ArgumentNullException.ThrowIfNull(manifestBytes);
        using var ms = new MemoryStream();
        ms.Write(manifestBytes);
        ms.WriteByte((byte)'\n');
        foreach (var path in files.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            ms.Write(Encoding.UTF8.GetBytes(path + "\n" + files[path] + "\n"));
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Every entry under <paramref name="folder"/>, recursively and hidden or system ones included, that is neither a
    /// listed file nor the root <c>plugin.json</c> or <c>signature.json</c>, as forward-slash relative paths in ordinal
    /// order. A folder counts only when it is a link (<see cref="IsFolderLink"/>), which is never recursed into: the
    /// signing tool refuses links, so a signed folder never needs one. Paths compare case-insensitively on Windows, as
    /// its file system does. An unreadable subfolder throws, which fails the check.
    /// </summary>
    private static List<string> Unlisted(string folder, IEnumerable<string> listed)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var covered = new HashSet<string>(listed, comparer) { "plugin.json", "signature.json" };
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = 0,
            IgnoreInaccessible = false,
            ReturnSpecialDirectories = false,
        };
        var entries = new System.IO.Enumeration.FileSystemEnumerable<(string Path, bool IsDirectory, bool IsLink)>(
            folder,
            (ref System.IO.Enumeration.FileSystemEntry entry) => (entry.ToFullPath(), entry.IsDirectory, IsFolderLink(ref entry)),
            options)
        {
            // Never into a link: what it points to is outside the folder the signature speaks for.
            ShouldRecursePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) => !IsFolderLink(ref entry),
        };
        var unlisted = new List<string>();
        foreach (var (path, isDirectory, isLink) in entries)
        {
            if (isDirectory && !isLink) continue;
            var relative = Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/');
            if (isDirectory || !covered.Contains(relative)) unlisted.Add(relative);
        }

        unlisted.Sort(StringComparer.Ordinal);
        return unlisted;
    }

    /// <summary>
    /// A junction or symbolic link to a folder. The reparse point attribute alone is not enough: OneDrive and other
    /// cloud-file placeholders set it on ordinary folders, so only a folder with a link target counts.
    /// </summary>
    private static bool IsFolderLink(ref System.IO.Enumeration.FileSystemEntry entry) =>
        entry.IsDirectory
        && (entry.Attributes & FileAttributes.ReparsePoint) != 0
        && new DirectoryInfo(entry.ToFullPath()).LinkTarget is not null;

    private static bool IsBareRelativePath(string p) =>
        !string.IsNullOrEmpty(p) && !Path.IsPathRooted(p) && !p.Contains('\\') && !p.StartsWith('/')
        && !p.Split('/').Any(s => s is "" or "." or "..") && p.IndexOfAny(Path.GetInvalidPathChars()) < 0;

    private static SignatureState Invalid(string error) => new(SignatureStatus.Invalid, null, error);
}
