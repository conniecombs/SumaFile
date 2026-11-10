using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Backend;
using SimpleFile.PluginHost.Manifest;
using SimpleFile.PluginHost.Trust;
using Xunit;
using Xunit.Abstractions;

namespace SimpleFile.Tests;

// Ed25519Verify is a process-wide static seam, so tests that touch it must not run in parallel
// with each other. They all live in this one class, which xunit runs serially.
public class PluginTrustTests(ITestOutputHelper output)
{
    private const string Id = "com.example.a";
    private const string Folder = @"C:\SumaFile\plugins\com.example.a";

    private static string NewTemp() =>
        Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    private static PluginManifest Manifest(string? publicKey)
    {
        var json = "{ \"id\": \"" + Id + "\", \"name\": \"N\", \"version\": \"1.0.0\", \"sdkVersion\": \"1.0.0\", " +
                   "\"publisher\": { \"name\": \"P\" }, \"contributions\": {} }";
        var m = PluginManifestReader.Read(json, Id).Manifest!;
        return m with { Publisher = m.Publisher with { PublicKey = publicKey } };
    }

    private static string Package(string tmp, out Dictionary<string, string> files)
    {
        var dir = Path.Combine(tmp, Id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), "{\"id\":\"" + Id + "\"}");
        File.WriteAllText(Path.Combine(dir, "a.dll"), "alpha");
        File.WriteAllText(Path.Combine(dir, "b.dll"), "beta");
        files = new Dictionary<string, string>
        {
            ["a.dll"] = Hex(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "a.dll")))),
            ["b.dll"] = Hex(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "b.dll")))),
        };
        return dir;
    }

    private static void WriteSignature(string dir, Dictionary<string, string> files, byte[] sig)
    {
        var sb = new StringBuilder("{ \"algorithm\": \"ed25519\", \"files\": {");
        var first = true;
        foreach (var (k, v) in files)
        {
            if (!first) sb.Append(',');
            first = false;
            sb.Append('"').Append(k).Append("\": \"").Append(v).Append('"');
        }
        sb.Append("}, \"signature\": \"").Append(Convert.ToBase64String(sig)).Append("\" }");
        File.WriteAllText(Path.Combine(dir, "signature.json"), sb.ToString());
    }

    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] Sig = Enumerable.Range(7, 64).Select(i => (byte)i).ToArray();

    [Fact]
    public void ComputeManifestHash_IsLowercaseSha256OfFile()
    {
        var tmp = NewTemp();
        try
        {
            var dir = Package(tmp, out _);
            var expected = Hex(SHA256.HashData(File.ReadAllBytes(Path.Combine(dir, "plugin.json"))));
            Assert.Equal(expected, PluginPackageHash.ComputeManifestHash(dir));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Verify_NoSignatureFile_IsUnsigned()
    {
        var tmp = NewTemp();
        try
        {
            var dir = Package(tmp, out _);
            var s = PluginSignatureVerifier.Verify(dir, Manifest(null));
            Assert.Equal(SignatureStatus.Unsigned, s.Status);
            Assert.Null(s.KeyFingerprint);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Verify_WellFormedPackage_IsVerifiedWhenPrimitiveAccepts()
    {
        var tmp = NewTemp();
        var original = PluginSignatureVerifier.Ed25519Verify;
        try
        {
            var dir = Package(tmp, out var files);
            WriteSignature(dir, files, Sig);
            byte[]? gotKey = null, gotPayload = null, gotSig = null;
            PluginSignatureVerifier.Ed25519Verify = (k, p, s) => { gotKey = k; gotPayload = p; gotSig = s; return true; };

            var state = PluginSignatureVerifier.Verify(dir, Manifest(Convert.ToBase64String(Key)));

            Assert.Equal(SignatureStatus.Verified, state.Status);
            Assert.Null(state.Error);
            Assert.Equal(Hex(SHA256.HashData(Key))[..16], state.KeyFingerprint);
            Assert.Equal(Key, gotKey);
            Assert.Equal(Sig, gotSig);
            Assert.Equal(PluginSignatureVerifier.CanonicalPayload(dir, files), gotPayload);
        }
        finally
        {
            PluginSignatureVerifier.Ed25519Verify = original;
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }

    [Theory]
    [InlineData("ui")]
    [InlineData("backend")]
    public void Verify_ValidSignatureNotCoveringDeclaredCode_IsInvalid(string kind)
    {
        var tmp = NewTemp();
        var original = PluginSignatureVerifier.Ed25519Verify;
        try
        {
            var dir = Package(tmp, out var files);
            File.WriteAllText(Path.Combine(dir, "unlisted.bin"), "swapped code");
            WriteSignature(dir, files, Sig);
            PluginSignatureVerifier.Ed25519Verify = (_, _, _) => true;
            var manifest = Manifest(Convert.ToBase64String(Key));
            manifest = kind == "ui"
                ? manifest with { Ui = new PluginUiSpec("unlisted.bin", "T") }
                : manifest with { Backend = new PluginBackendSpec("unlisted.bin", [], "first-call", true) };

            var state = PluginSignatureVerifier.Verify(dir, manifest);

            Assert.Equal(SignatureStatus.Invalid, state.Status);
            Assert.Contains("unlisted.bin", state.Error);
        }
        finally
        {
            PluginSignatureVerifier.Ed25519Verify = original;
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }

    [Fact]
    public void Verify_ValidSignatureCoveringDeclaredCode_IsVerified()
    {
        var tmp = NewTemp();
        var original = PluginSignatureVerifier.Ed25519Verify;
        try
        {
            var dir = Package(tmp, out var files);
            WriteSignature(dir, files, Sig);
            PluginSignatureVerifier.Ed25519Verify = (_, _, _) => true;
            var manifest = Manifest(Convert.ToBase64String(Key)) with { Ui = new PluginUiSpec("a.dll", "T") };

            Assert.Equal(SignatureStatus.Verified, PluginSignatureVerifier.Verify(dir, manifest).Status);
        }
        finally
        {
            PluginSignatureVerifier.Ed25519Verify = original;
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }

    [Fact]
    public void Verify_TamperedListedFile_IsInvalid()
    {
        var tmp = NewTemp();
        var original = PluginSignatureVerifier.Ed25519Verify;
        try
        {
            var dir = Package(tmp, out var files);
            WriteSignature(dir, files, Sig);
            File.WriteAllText(Path.Combine(dir, "a.dll"), "tampered");
            var called = false;
            PluginSignatureVerifier.Ed25519Verify = (_, _, _) => { called = true; return true; };

            var state = PluginSignatureVerifier.Verify(dir, Manifest(Convert.ToBase64String(Key)));

            Assert.Equal(SignatureStatus.Invalid, state.Status);
            Assert.NotNull(state.Error);
            Assert.False(called);
        }
        finally
        {
            PluginSignatureVerifier.Ed25519Verify = original;
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }

    private static (byte[] Pub, Ed25519PrivateKeyParameters Priv) NewKeyPair()
    {
        var gen = new Ed25519KeyPairGenerator();
        gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
        var pair = gen.GenerateKeyPair();
        return (((Ed25519PublicKeyParameters)pair.Public).GetEncoded(), (Ed25519PrivateKeyParameters)pair.Private);
    }

    private static byte[] Sign(Ed25519PrivateKeyParameters priv, byte[] payload)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, priv);
        signer.BlockUpdate(payload, 0, payload.Length);
        return signer.GenerateSignature();
    }

    [Fact]
    public void Verify_ValidSignature_IsVerifiedWithFingerprint()
    {
        var tmp = NewTemp();
        try
        {
            var dir = Package(tmp, out var files);
            var (pub, priv) = NewKeyPair();
            WriteSignature(dir, files, Sign(priv, PluginSignatureVerifier.CanonicalPayload(dir, files)));

            var state = PluginSignatureVerifier.Verify(dir, Manifest(Convert.ToBase64String(pub)));

            Assert.Equal(SignatureStatus.Verified, state.Status);
            Assert.Null(state.Error);
            Assert.Equal(Hex(SHA256.HashData(pub))[..16], state.KeyFingerprint);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Verify_WithTheBytesDiscoveryRead_SignsThoseBytesAndReadsNoManifest()
    {
        // Spec: a scan reads each plugin.json once. The verifier checks the bytes discovery parsed and hashed, so a
        // manifest swapped on disk after the read cannot be judged apart from the hash the approval used.
        var tmp = NewTemp();
        try
        {
            var dir = Package(tmp, out var files);
            var (pub, priv) = NewKeyPair();
            WriteSignature(dir, files, Sign(priv, PluginSignatureVerifier.CanonicalPayload(dir, files)));
            var read = File.ReadAllBytes(Path.Combine(dir, "plugin.json"));
            Assert.Equal(PluginSignatureVerifier.CanonicalPayload(dir, files), PluginSignatureVerifier.CanonicalPayload(read, files));
            File.WriteAllText(Path.Combine(dir, "plugin.json"), "{\"id\":\"" + Id + "\",\"evil\":true}");

            Assert.Equal(SignatureStatus.Verified, PluginSignatureVerifier.Verify(dir, Manifest(Convert.ToBase64String(pub)), read).Status);
            Assert.Equal(SignatureStatus.Invalid, PluginSignatureVerifier.Verify(dir, Manifest(Convert.ToBase64String(pub))).Status);

            // Without a plugin.json on disk at all, the discovery bytes are still enough.
            File.Delete(Path.Combine(dir, "plugin.json"));
            Assert.Equal(SignatureStatus.Verified, PluginSignatureVerifier.Verify(dir, Manifest(Convert.ToBase64String(pub)), read).Status);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Discovery_KeepsTheManifestBytesItHashed()
    {
        var tmp = NewTemp();
        try
        {
            var root = Path.Combine(tmp, "plugins");
            var folder = WriteContributingPlugin(root, "com.example.read");
            var expected = File.ReadAllBytes(Path.Combine(folder, "plugin.json"));

            var plugin = Assert.Single(SimpleFile.PluginHost.Discovery.PluginDiscovery.Scan([root]));

            Assert.Equal(expected, plugin.ManifestBytes);
            Assert.Equal(Hex(SHA256.HashData(expected)), plugin.ManifestHash);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Verify_TamperedManifest_IsInvalid()
    {
        var tmp = NewTemp();
        try
        {
            var dir = Package(tmp, out var files);
            var (pub, priv) = NewKeyPair();
            WriteSignature(dir, files, Sign(priv, PluginSignatureVerifier.CanonicalPayload(dir, files)));
            File.WriteAllText(Path.Combine(dir, "plugin.json"), "{\"id\":\"" + Id + "\",\"evil\":true}");

            var state = PluginSignatureVerifier.Verify(dir, Manifest(Convert.ToBase64String(pub)));

            Assert.Equal(SignatureStatus.Invalid, state.Status);
            Assert.NotNull(state.Error);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Verify_WrongKey_IsInvalid()
    {
        var tmp = NewTemp();
        try
        {
            var dir = Package(tmp, out var files);
            var (_, privA) = NewKeyPair();
            var (pubB, _) = NewKeyPair();
            WriteSignature(dir, files, Sign(privA, PluginSignatureVerifier.CanonicalPayload(dir, files)));

            var state = PluginSignatureVerifier.Verify(dir, Manifest(Convert.ToBase64String(pubB)));

            Assert.Equal(SignatureStatus.Invalid, state.Status);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Verify_PrimitiveThrows_IsInvalidWithMessage()
    {
        var tmp = NewTemp();
        var original = PluginSignatureVerifier.Ed25519Verify;
        try
        {
            var dir = Package(tmp, out var files);
            WriteSignature(dir, files, Sig);
            PluginSignatureVerifier.Ed25519Verify = (_, _, _) => throw new InvalidCastException("boom");

            var state = PluginSignatureVerifier.Verify(dir, Manifest(Convert.ToBase64String(Key)));

            Assert.Equal(SignatureStatus.Invalid, state.Status);
            Assert.Contains("boom", state.Error);
        }
        finally
        {
            PluginSignatureVerifier.Ed25519Verify = original;
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }

    [Fact]
    public void Ed25519Verify_RejectsNull() =>
        Assert.Throws<ArgumentNullException>(() => PluginSignatureVerifier.Ed25519Verify = null!);

    [Fact]
    public void Load_NullRecord_IsTreatedAsAbsent()
    {
        var tmp = NewTemp();
        try
        {
            Directory.CreateDirectory(tmp);
            var path = Path.Combine(tmp, "a.json");
            File.WriteAllText(path, "{ \"" + Id + "\": null }");
            var store = new PluginApprovalStore(path);
            Assert.Equal(ApprovalState.NeedsConsent, store.Resolve(Id, "h", SignatureStatus.Unsigned, Folder));
            store.Set(Id, "h", SignatureStatus.Unsigned, ApprovalState.Enabled, Folder);
            Assert.Equal(ApprovalState.Enabled, store.Resolve(Id, "h", SignatureStatus.Unsigned, Folder));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Verify_SignatureWithoutPublicKey_IsInvalid()
    {
        var tmp = NewTemp();
        var original = PluginSignatureVerifier.Ed25519Verify;
        try
        {
            var dir = Package(tmp, out var files);
            WriteSignature(dir, files, Sig);
            var called = false;
            PluginSignatureVerifier.Ed25519Verify = (_, _, _) => { called = true; return true; };

            var state = PluginSignatureVerifier.Verify(dir, Manifest(null));

            Assert.Equal(SignatureStatus.Invalid, state.Status);
            Assert.NotNull(state.Error);
            Assert.False(called);
        }
        finally
        {
            PluginSignatureVerifier.Ed25519Verify = original;
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
    }

    [Fact]
    public void Resolve_NoRecord_NeedsConsent()
    {
        var tmp = NewTemp();
        try
        {
            var store = new PluginApprovalStore(Path.Combine(tmp, "a.json"));
            Assert.Equal(ApprovalState.NeedsConsent, store.Resolve(Id, "h", SignatureStatus.Unsigned, Folder));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Resolve_RecordMatches_ReturnsStoredState()
    {
        var tmp = NewTemp();
        try
        {
            var store = new PluginApprovalStore(Path.Combine(tmp, "a.json"));
            store.Set(Id, "h", SignatureStatus.Unsigned, ApprovalState.Enabled, Folder);
            Assert.Equal(ApprovalState.Enabled, store.Resolve(Id, "h", SignatureStatus.Unsigned, Folder));
            store.Set(Id, "h", SignatureStatus.Unsigned, ApprovalState.Disabled, Folder);
            Assert.Equal(ApprovalState.Disabled, store.Resolve(Id, "h", SignatureStatus.Unsigned, Folder));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Resolve_HashChanged_NeedsConsent()
    {
        var tmp = NewTemp();
        try
        {
            var store = new PluginApprovalStore(Path.Combine(tmp, "a.json"));
            store.Set(Id, "h", SignatureStatus.Unsigned, ApprovalState.Enabled, Folder);
            Assert.Equal(ApprovalState.NeedsConsent, store.Resolve(Id, "other", SignatureStatus.Unsigned, Folder));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Resolve_SignatureStatusChanged_NeedsConsent()
    {
        var tmp = NewTemp();
        try
        {
            var store = new PluginApprovalStore(Path.Combine(tmp, "a.json"));
            store.Set(Id, "h", SignatureStatus.Unsigned, ApprovalState.Enabled, Folder);
            Assert.Equal(ApprovalState.NeedsConsent, store.Resolve(Id, "h", SignatureStatus.Verified, Folder));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Set_PersistsAcrossInstances()
    {
        var tmp = NewTemp();
        try
        {
            var path = Path.Combine(tmp, "sub", "a.json");
            new PluginApprovalStore(path).Set(Id, "h", SignatureStatus.Verified, ApprovalState.Enabled, Folder);
            var again = new PluginApprovalStore(path);
            Assert.Equal(ApprovalState.Enabled, again.Resolve(Id, "h", SignatureStatus.Verified, Folder));
            again.Remove(Id);
            Assert.Equal(ApprovalState.NeedsConsent, new PluginApprovalStore(path).Resolve(Id, "h", SignatureStatus.Verified, Folder));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Load_CorruptFile_IsTreatedAsEmpty()
    {
        var tmp = NewTemp();
        try
        {
            Directory.CreateDirectory(tmp);
            var path = Path.Combine(tmp, "a.json");
            File.WriteAllText(path, "{ not json");
            Assert.Equal(ApprovalState.NeedsConsent, new PluginApprovalStore(path).Resolve(Id, "h", SignatureStatus.Unsigned, Folder));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void ApprovalStore_ReadsTheShapeTheUiSmokeWrites()
    {
        // scripts/smoke-winui-ui.ps1 -PluginsDir writes this by hand; keep the two in step.
        var tmp = Path.Combine(Path.GetTempPath(), "sumafile-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var path = Path.Combine(tmp, "plugin-approvals.json");
            File.WriteAllText(path, "{ \"" + Id + "\": { \"ManifestHash\": \"abc\", \"SignatureStatus\": \"Unsigned\", " +
                "\"State\": \"Enabled\", \"UpdatedAt\": \"2026-11-07T00:00:00+00:00\", " +
                "\"Folder\": " + System.Text.Json.JsonSerializer.Serialize(Folder) + " } }");
            Assert.Equal(ApprovalState.Enabled, new PluginApprovalStore(path).Resolve(Id, "abc", SignatureStatus.Unsigned, Folder));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void ApprovalStore_RecordForAnotherFolder_NeedsConsent()
    {
        var tmp = NewTemp();
        try
        {
            var store = new PluginApprovalStore(Path.Combine(tmp, "a.json"));
            store.Set(Id, "h", SignatureStatus.Unsigned, ApprovalState.Enabled, Folder);

            Assert.Equal(ApprovalState.Enabled, store.Resolve(Id, "h", SignatureStatus.Unsigned, Folder.ToUpperInvariant() + "\\"));
            Assert.Equal(ApprovalState.NeedsConsent,
                store.Resolve(Id, "h", SignatureStatus.Unsigned, @"D:\Elsewhere\plugins\com.example.a"));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void ApprovalStore_RecordWithoutFolder_NeedsConsent()
    {
        var tmp = NewTemp();
        Directory.CreateDirectory(tmp);
        try
        {
            var path = Path.Combine(tmp, "plugin-approvals.json");
            File.WriteAllText(path, "{ \"" + Id + "\": { \"ManifestHash\": \"abc\", \"SignatureStatus\": \"Unsigned\", " +
                "\"State\": \"Enabled\", \"UpdatedAt\": \"2026-11-07T00:00:00+00:00\" } }");
            Assert.Equal(ApprovalState.NeedsConsent, new PluginApprovalStore(path).Resolve(Id, "abc", SignatureStatus.Unsigned, Folder));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = string.Concat(Enumerable.Repeat("0123456789abcdef", 4));

    [Fact]
    public void BundledPluginList_ReadsIdsAndHashes_AndSkipsMalformedEntries()
    {
        var tmp = NewTemp();
        try
        {
            Directory.CreateDirectory(tmp);
            File.WriteAllText(Path.Combine(tmp, BundledPluginList.FileName), $$"""
                { "plugins": [
                    { "id": "com.sumafile.sevenzip", "manifestSha256": "{{HashA}}" },
                    { "id": "com.sumafile.other", "manifestSha256": "{{HashB.ToUpperInvariant()}}" },
                    { "id": "com.sumafile.short", "manifestSha256": "abc" },
                    { "id": "com.sumafile.nothex", "manifestSha256": "{{new string('g', 64)}}" },
                    { "id": "Bad Id", "manifestSha256": "{{HashA}}" },
                    { "id": "com.sumafile.nohash" },
                    { "manifestSha256": "{{HashA}}" },
                    { "id": 7, "manifestSha256": "{{HashA}}" },
                    { "id": "com.sumafile.numhash", "manifestSha256": 12 },
                    "not an object",
                    null
                ] }
                """);

            var list = BundledPluginList.Read(tmp);

            Assert.Equal(2, list.Count);
            Assert.Equal(HashA, list["com.sumafile.sevenzip"]);
            // Hex is compared lowercase, as PluginPackageHash computes it.
            Assert.Equal(HashB, list["com.sumafile.other"]);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("{ \"plugins\": {} }")]
    [InlineData("{ \"plugins\": null }")]
    [InlineData("{ \"other\": [] }")]
    public void BundledPluginList_MissingOrCorruptFile_IsEmpty(string? content)
    {
        var tmp = NewTemp();
        try
        {
            Directory.CreateDirectory(tmp);
            if (content is not null) File.WriteAllText(Path.Combine(tmp, BundledPluginList.FileName), content);

            Assert.Empty(BundledPluginList.Read(tmp));
            Assert.Empty(BundledPluginList.Read(Path.Combine(tmp, "missing-root")));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void BundledList_WithUtf8Bom_ListsItsPlugins()
    {
        var tmp = NewTemp();
        try
        {
            Directory.CreateDirectory(tmp);
            // Windows PowerShell's Set-Content -Encoding UTF8 and Out-File write this BOM; a list saved that way
            // must still trust its plugins.
            var json = Encoding.UTF8.GetBytes($$"""{"plugins":[{"id":"com.a.b","manifestSha256":"{{HashA}}"}]}""");
            File.WriteAllBytes(Path.Combine(tmp, BundledPluginList.FileName), [0xEF, 0xBB, 0xBF, .. json]);

            var list = BundledPluginList.Read(tmp);

            Assert.Equal(HashA, Assert.Contains("com.a.b", list));
            Assert.Single(list);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void BundledList_WithTwoPlugins_ListsBoth()
    {
        var tmp = NewTemp();
        try
        {
            Directory.CreateDirectory(tmp);
            // Exactly what Write-BundledPluginList in scripts/lib/plugin-staging.ps1 writes for two plugins.
            File.WriteAllText(Path.Combine(tmp, BundledPluginList.FileName),
                $$"""{"plugins":[{"id":"com.sumafile.sevenzip","manifestSha256":"{{HashA}}"},{"id":"com.sumafile.remote","manifestSha256":"{{HashB.ToUpperInvariant()}}"}]}""",
                new UTF8Encoding(false));

            var list = BundledPluginList.Read(tmp);

            Assert.Equal(2, list.Count);
            Assert.Equal(HashA, list["com.sumafile.sevenzip"]);
            Assert.Equal(HashB, list["com.sumafile.remote"]);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task BundledPlugins_TwoListedPlugins_AreBothEnabledWithoutConsent()
    {
        var tmp = NewTemp();
        try
        {
            var root = Path.Combine(tmp, "plugins");
            var approvals = Path.Combine(tmp, "approvals.json");
            string[] ids = ["com.example.first", "com.example.second"];
            var entries = new List<string>();
            foreach (var id in ids)
            {
                var folder = Path.Combine(root, id);
                Directory.CreateDirectory(folder);
                // No backend and no UI assembly, so enabling starts nothing.
                File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""
                    {
                      "id": "{{id}}",
                      "name": "{{id}}",
                      "version": "1.0.0",
                      "publisher": { "name": "SumaFile Team" },
                      "sdkVersion": "1.0.0",
                      "contributions": { "commands": [] }
                    }
                    """);
                entries.Add($$"""{"id":"{{id}}","manifestSha256":"{{PluginPackageHash.ComputeManifestHash(folder)}}"}""");
            }

            File.WriteAllText(Path.Combine(root, BundledPluginList.FileName),
                "{\"plugins\":[" + string.Join(",", entries) + "]}", new UTF8Encoding(false));

            await using var manager = new PluginManager(new PluginManagerOptions
            {
                Roots = [root],
                ApprovalStorePath = approvals,
                DataRoot = Path.Combine(tmp, "data"),
                Launcher = new NeverLaunches(),
                PipeNameFor = _ => "SumaFile.Plugin.Test." + Guid.NewGuid().ToString("N"),
                Settings = new FakeSettings(),
                Progress = new RecordingSink(),
                Notifier = new RecordingNotifier(),
                DialogsFor = _ => null!,
                Workspace = null!,
                Log = (_, _) => { },
                BundledRoot = root,
            });

            await manager.DiscoverAsync(CancellationToken.None);

            Assert.Equal(ids, manager.EnabledPlugins.Select(p => p.Id).Order(StringComparer.Ordinal));
            Assert.All(manager.Plugins, p => Assert.Equal(PluginState.Enabled, p.State));
            Assert.False(File.Exists(approvals), "bundled trust must not write the approvals file");
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    /// <summary>
    /// A plugin without a backend or UI assembly (nothing to launch) whose manifest carries a command, a toolbar
    /// item, a context-menu item and three settings, like spec §5.2's startup budget case.
    /// </summary>
    private static string WriteContributingPlugin(string root, string id, string? publicKey = null)
    {
        var folder = Path.Combine(root, id);
        Directory.CreateDirectory(folder);
        var key = publicKey is null ? "" : $$""", "publicKey": "{{publicKey}}" """;
        File.WriteAllText(Path.Combine(folder, "plugin.json"), $$"""
            {
              "id": "{{id}}",
              "name": "{{id}}",
              "version": "1.0.0",
              "publisher": { "name": "P"{{key}} },
              "sdkVersion": "1.0.0",
              "contributions": {
                "commands": [ { "id": "run", "label": "Run {{id}}", "group": "Budget" } ],
                "toolbar": [ { "command": "run", "label": "Run", "glyph": "\uE768", "placement": "overflow" } ],
                "contextMenu": [ { "group": "Budget", "items": [ { "command": "run", "label": "Run here", "when": "selectionCount >= 1" } ] } ]
              },
              "settings": [
                { "key": "greeting", "type": "string", "label": "Greeting", "default": "Hello", "group": "General" },
                { "key": "loud", "type": "boolean", "label": "Shout", "default": false, "group": "General" },
                { "key": "count", "type": "integer", "label": "Count", "default": 3, "group": "General" }
              ]
            }
            """);
        return folder;
    }

    private static void WriteBundledList(string root, IEnumerable<string> folders)
    {
        var entries = folders.Select(folder =>
            $$"""{"id":"{{Path.GetFileName(folder)}}","manifestSha256":"{{PluginPackageHash.ComputeManifestHash(folder)}}"}""");
        File.WriteAllText(Path.Combine(root, BundledPluginList.FileName),
            "{\"plugins\":[" + string.Join(",", entries) + "]}", new UTF8Encoding(false));
    }

    private static PluginManager NewManager(string tmp, string root, string approvals) =>
        new(new PluginManagerOptions
        {
            Roots = [root],
            ApprovalStorePath = approvals,
            DataRoot = Path.Combine(tmp, "data"),
            Launcher = new NeverLaunches(),
            PipeNameFor = _ => "SumaFile.Plugin.Test." + Guid.NewGuid().ToString("N"),
            Settings = new FakeSettings(),
            Progress = new RecordingSink(),
            Notifier = new RecordingNotifier(),
            DialogsFor = _ => null!,
            Workspace = null!,
            Log = (_, _) => { },
            BundledRoot = root,
        });

    [Fact]
    public async Task Discover_ReadsTheApprovalsFileOncePerScan()
    {
        var tmp = NewTemp();
        try
        {
            var root = Path.Combine(tmp, "plugins");
            var approvals = Path.Combine(tmp, "approvals.json");
            var bundled = WriteContributingPlugin(root, "com.example.first");
            var approved = WriteContributingPlugin(root, "com.example.second");
            WriteContributingPlugin(root, "com.example.third");
            WriteBundledList(root, [bundled]);
            new PluginApprovalStore(approvals).Set("com.example.second", PluginPackageHash.ComputeManifestHash(approved),
                SignatureStatus.Unsigned, ApprovalState.Enabled, approved);

            await using var manager = NewManager(tmp, root, approvals);
            await manager.DiscoverAsync(CancellationToken.None);

            Assert.Equal(1, manager.Approvals.LoadCount);
            Assert.Equal(
                [PluginState.Enabled, PluginState.Enabled, PluginState.NeedsConsent],
                manager.Plugins.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => p.State));

            await manager.RescanAsync(CancellationToken.None);
            Assert.Equal(2, manager.Approvals.LoadCount);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task Discover_WithAPreparedScan_ReadsNothingAgain()
    {
        // Budget fallback F2: the App prepares the scan on the thread pool while the file service starts, so its
        // plugins-* startup span reads nothing.
        var tmp = NewTemp();
        try
        {
            var root = Path.Combine(tmp, "plugins");
            var approvals = Path.Combine(tmp, "approvals.json");
            var bundled = WriteContributingPlugin(root, "com.example.first");
            var approved = WriteContributingPlugin(root, "com.example.second");
            var third = WriteContributingPlugin(root, "com.example.third");
            WriteBundledList(root, [bundled]);
            new PluginApprovalStore(approvals).Set("com.example.second", PluginPackageHash.ComputeManifestHash(approved),
                SignatureStatus.Unsigned, ApprovalState.Enabled, approved);

            var prepared = PluginManager.PrepareScan([root], root, approvals);
            Assert.Equal(1, prepared.Approvals.LoadCount);

            // Change everything the scan read: a second read of any of it would change the outcome.
            File.Delete(Path.Combine(root, BundledPluginList.FileName));
            File.Delete(approvals);
            File.WriteAllText(Path.Combine(third, "plugin.json"), "{ not json");

            await using var manager = NewManager(tmp, root, approvals);
            await manager.DiscoverAsync(prepared, CancellationToken.None);

            Assert.Equal(0, manager.Approvals.LoadCount);
            Assert.Equal(1, prepared.Approvals.LoadCount);
            Assert.Equal(
                [PluginState.Enabled, PluginState.Enabled, PluginState.NeedsConsent],
                manager.Plugins.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => p.State));

            // A rescan never reuses it.
            await manager.RescanAsync(CancellationToken.None);
            Assert.Equal(1, manager.Approvals.LoadCount);
            Assert.Equal(
                [PluginState.NeedsConsent, PluginState.NeedsConsent, PluginState.InvalidManifest],
                manager.Plugins.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => p.State));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task Discover_WithAPreparedScanForOtherInputs_ScansAgain()
    {
        var tmp = NewTemp();
        try
        {
            var root = Path.Combine(tmp, "plugins");
            var approvals = Path.Combine(tmp, "approvals.json");
            WriteContributingPlugin(root, "com.example.first");
            var prepared = PluginManager.PrepareScan([Path.Combine(tmp, "elsewhere")], root, approvals);

            await using var manager = NewManager(tmp, root, approvals);
            await manager.DiscoverAsync(prepared, CancellationToken.None);

            // Prepared for another root: ignored, and this manager reads for itself.
            Assert.Equal(1, manager.Approvals.LoadCount);
            Assert.Equal("com.example.first", Assert.Single(manager.Plugins).Id);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task Discover_WithAPreparedScan_CompletesOnTheCallersThread()
    {
        // The prepared scan was read and judged on the thread pool already, so the App's startup span holds no hop to
        // the pool and no hop back to the UI thread: discovery completes, and raises Changed, before it returns.
        var tmp = NewTemp();
        try
        {
            var root = Path.Combine(tmp, "plugins");
            var approvals = Path.Combine(tmp, "approvals.json");
            var bundled = WriteContributingPlugin(root, "com.example.first");
            WriteContributingPlugin(root, "com.example.second");
            WriteBundledList(root, [bundled]);
            var prepared = PluginManager.PrepareScan([root], root, approvals);

            await using var manager = NewManager(tmp, root, approvals);
            var caller = Environment.CurrentManagedThreadId;
            var changedOn = -1;
            manager.Changed += (_, _) => changedOn = Environment.CurrentManagedThreadId;
            var discover = manager.DiscoverAsync(prepared, startBackends: false, CancellationToken.None);

            Assert.True(discover.IsCompletedSuccessfully, "discovery from a prepared scan must not wait for anything");
            Assert.Equal(caller, changedOn);
            await discover;
            Assert.Equal(
                [PluginState.Enabled, PluginState.NeedsConsent],
                manager.Plugins.OrderBy(p => p.Id, StringComparer.Ordinal).Select(p => p.State));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task PrepareScan_VerifiesSignaturesBeforeDiscovery()
    {
        // PrepareScan judges every plugin (signature hashing included) while the file service starts; discovery from
        // it checks nothing again, and a rescan verifies afresh.
        var tmp = NewTemp();
        try
        {
            var root = Path.Combine(tmp, "plugins");
            var approvals = Path.Combine(tmp, "approvals.json");
            var (pub, priv) = NewKeyPair();
            var folder = WriteContributingPlugin(root, "com.example.signed", Convert.ToBase64String(pub));
            var payload = Encoding.UTF8.GetBytes("signed payload");
            File.WriteAllBytes(Path.Combine(folder, "payload.bin"), payload);
            var files = new Dictionary<string, string> { ["payload.bin"] = Hex(SHA256.HashData(payload)) };
            WriteSignature(folder, files, Sign(priv, PluginSignatureVerifier.CanonicalPayload(folder, files)));
            new PluginApprovalStore(approvals).Set("com.example.signed", PluginPackageHash.ComputeManifestHash(folder),
                SignatureStatus.Verified, ApprovalState.Enabled, folder);

            var prepared = PluginManager.PrepareScan([root], root, approvals);
            // A verification after this point fails: the listed file no longer matches its recorded hash.
            File.WriteAllText(Path.Combine(folder, "payload.bin"), "tampered");

            await using var manager = NewManager(tmp, root, approvals);
            await manager.DiscoverAsync(prepared, startBackends: false, CancellationToken.None);

            var record = Assert.Single(manager.Plugins);
            Assert.Equal(SignatureStatus.Verified, record.Signature.Status);
            Assert.Equal(PluginState.Enabled, record.State);

            await manager.RescanAsync(CancellationToken.None);

            record = Assert.Single(manager.Plugins);
            Assert.Equal(SignatureStatus.Invalid, record.Signature.Status);
            Assert.Equal(PluginState.NeedsConsent, record.State);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void ApprovalStore_KeepsItsFileFormat()
    {
        var tmp = NewTemp();
        Directory.CreateDirectory(tmp);
        try
        {
            const string other = "com.example.other";
            const string otherFolder = @"C:\SumaFile\plugins\com.example.other";
            var path = Path.Combine(tmp, "plugin-approvals.json");
            // Today's format: PascalCase keys, string enums, an ISO date and the folder.
            File.WriteAllText(path, "{\n  \"" + Id + "\": {\n    \"ManifestHash\": \"abc\",\n    \"SignatureStatus\": \"Unsigned\",\n" +
                "    \"State\": \"Enabled\",\n    \"UpdatedAt\": \"2026-11-07T00:00:00+00:00\",\n" +
                "    \"Folder\": " + System.Text.Json.JsonSerializer.Serialize(Folder) + "\n  },\n" +
                "  \"" + other + "\": {\n    \"ManifestHash\": \"def\",\n    \"SignatureStatus\": \"Verified\",\n" +
                "    \"State\": \"Disabled\",\n    \"UpdatedAt\": \"2026-11-07T00:00:00+00:00\",\n" +
                "    \"Folder\": " + System.Text.Json.JsonSerializer.Serialize(otherFolder) + "\n  }\n}");
            var store = new PluginApprovalStore(path);

            Assert.Equal(ApprovalState.Enabled, store.Resolve(Id, "abc", SignatureStatus.Unsigned, Folder));
            Assert.Equal(ApprovalState.Disabled, store.Resolve(other, "def", SignatureStatus.Verified, otherFolder));

            store.Set("com.example.third", "ghi", SignatureStatus.Invalid, ApprovalState.Enabled, @"C:\SumaFile\plugins\com.example.third");

            var text = File.ReadAllText(path);
            Assert.Contains("\n  \"com.example.third\": {", text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            Assert.Equal([Id, other, "com.example.third"], doc.RootElement.EnumerateObject().Select(p => p.Name));
            foreach (var record in doc.RootElement.EnumerateObject())
            {
                Assert.Equal(
                    ["ManifestHash", "SignatureStatus", "State", "UpdatedAt", "Folder"],
                    record.Value.EnumerateObject().Select(p => p.Name));
                Assert.All(record.Value.EnumerateObject(), p => Assert.Equal(System.Text.Json.JsonValueKind.String, p.Value.ValueKind));
            }

            var third = doc.RootElement.GetProperty("com.example.third");
            Assert.Equal("Invalid", third.GetProperty("SignatureStatus").GetString());
            Assert.Equal("Enabled", third.GetProperty("State").GetString());
            Assert.Equal(@"C:\SumaFile\plugins\com.example.third", third.GetProperty("Folder").GetString());
            Assert.Equal("Disabled", doc.RootElement.GetProperty(other).GetProperty("State").GetString());
            Assert.Equal("def", doc.RootElement.GetProperty(other).GetProperty("ManifestHash").GetString());
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task Discover_TenPlugins_StaysUnderTheStartupBudget()
    {
        // Spec §5.2: "Manifest reading and contribution rendering happen at startup and must stay under 50 ms
        // for ten plugins in the startup trace."
        var tmp = NewTemp();
        try
        {
            var root = Path.Combine(tmp, "plugins");
            var approvals = Path.Combine(tmp, "approvals.json");
            var (pub, priv) = NewKeyPair();
            var publicKey = Convert.ToBase64String(pub);
            var payload = new byte[4 * 1024 * 1024];
            new Random(42).NextBytes(payload);

            var folders = new List<string>();
            for (var i = 0; i < 10; i++)
            {
                var signed = i is 2 or 3;
                var folder = WriteContributingPlugin(root, $"com.example.budget{i}", signed ? publicKey : null);
                if (signed)
                {
                    File.WriteAllBytes(Path.Combine(folder, "payload.bin"), payload);
                    var files = new Dictionary<string, string> { ["payload.bin"] = Hex(SHA256.HashData(payload)) };
                    WriteSignature(folder, files, Sign(priv, PluginSignatureVerifier.CanonicalPayload(folder, files)));
                }

                folders.Add(folder);
            }

            // Two bundled, six approved (two of them signed), two that need consent.
            WriteBundledList(root, folders.Take(2));
            var store = new PluginApprovalStore(approvals);
            for (var i = 2; i < 8; i++)
            {
                store.Set(Path.GetFileName(folders[i]), PluginPackageHash.ComputeManifestHash(folders[i]),
                    i is 2 or 3 ? SignatureStatus.Verified : SignatureStatus.Unsigned, ApprovalState.Enabled, folders[i]);
            }

            var cold = await TimeDiscoveryAsync();
            var runs = new List<double>();
            for (var run = 0; run < 5; run++) runs.Add(await TimeDiscoveryAsync());

            var best = runs.Min();
            output.WriteLine($"Ten-plugin discovery: cold {cold:0.0} ms; best of five {best:0.0} ms (runs: {string.Join(", ", runs.Select(r => r.ToString("0.0")))}).");
            Assert.True(best < 50, $"best ten-plugin discovery took {best:0.0} ms; budget is 50 ms");

            async Task<double> TimeDiscoveryAsync()
            {
                await using var manager = NewManager(tmp, root, approvals);
                var watch = System.Diagnostics.Stopwatch.StartNew();
                await manager.DiscoverAsync(CancellationToken.None);
                watch.Stop();

                Assert.Equal(1, manager.Approvals.LoadCount);
                var states = manager.Plugins.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
                Assert.Equal(10, states.Count);
                Assert.All(states.Take(8), p => Assert.Equal(PluginState.Enabled, p.State));
                Assert.All(states.Skip(8), p => Assert.Equal(PluginState.NeedsConsent, p.State));
                Assert.Equal(SignatureStatus.Verified, states[2].Signature.Status);
                Assert.Equal(SignatureStatus.Verified, states[3].Signature.Status);
                return watch.Elapsed.TotalMilliseconds;
            }
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    private sealed class NeverLaunches : IPluginProcessLauncher
    {
        public IPluginProcess Launch(
            string executable,
            IReadOnlyList<string> args,
            string workingDirectory,
            IReadOnlyDictionary<string, string> environment,
            Action<string> onStderrLine) =>
            throw new InvalidOperationException("a plugin without a backend must not launch one");
    }

    [Fact]
    public void StoredState_IgnoresHashButRequiresTheSameFolder()
    {
        var tmp = NewTemp();
        try
        {
            var store = new PluginApprovalStore(Path.Combine(tmp, "a.json"));
            Assert.Null(store.StoredState(Id, Folder));

            store.Set(Id, "old-hash", SignatureStatus.Verified, ApprovalState.Disabled, Folder);

            // Another hash and signature state no longer resolve, but the stored decision is still there.
            Assert.Equal(ApprovalState.NeedsConsent, store.Resolve(Id, "new-hash", SignatureStatus.Unsigned, Folder));
            Assert.Equal(ApprovalState.Disabled, store.StoredState(Id, Folder));
            Assert.Equal(ApprovalState.Disabled, store.StoredState(Id, Folder.ToUpperInvariant() + "\\"));
            Assert.Null(store.StoredState(Id, @"D:\Elsewhere\plugins\com.example.a"));
            Assert.Null(store.StoredState("com.example.other", Folder));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Set_WhenTheFileCannotBeRead_ThrowsAndKeepsOtherApprovals()
    {
        var tmp = NewTemp();
        try
        {
            var path = Path.Combine(tmp, "a.json");
            var store = new PluginApprovalStore(path);
            const string other = "com.example.other";
            const string otherFolder = @"C:\SumaFile\plugins\com.example.other";
            store.Set(other, "h", SignatureStatus.Unsigned, ApprovalState.Enabled, otherFolder);

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var error = Assert.Throws<IOException>(() =>
                    store.Set(Id, "h", SignatureStatus.Unsigned, ApprovalState.Enabled, Folder));
                Assert.StartsWith("Could not read the plugin approvals file: ", error.Message, StringComparison.Ordinal);
                Assert.Throws<IOException>(() => store.Remove(other));

                // Reads degrade to an empty store instead of failing.
                Assert.Equal(ApprovalState.NeedsConsent, store.Resolve(other, "h", SignatureStatus.Unsigned, otherFolder));
                Assert.Null(store.StoredState(other, otherFolder));
                // ...but a caller that must fail safe can tell an unreadable store from an empty one.
                Assert.Null(store.StoredState(other, otherFolder, out var unreadable));
                Assert.True(unreadable);
            }

            Assert.Equal(ApprovalState.Enabled, store.StoredState(other, otherFolder, out var unreadableAfter));
            Assert.False(unreadableAfter);
            Assert.Null(store.StoredState("com.example.missing", otherFolder, out var unreadableMissing));
            Assert.False(unreadableMissing);

            var again = new PluginApprovalStore(path);
            Assert.Equal(ApprovalState.Enabled, again.Resolve(other, "h", SignatureStatus.Unsigned, otherFolder));
            Assert.Equal(ApprovalState.NeedsConsent, again.Resolve(Id, "h", SignatureStatus.Unsigned, Folder));
            Assert.Equal(["a.json"], Directory.GetFiles(tmp).Select(Path.GetFileName));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }

    [Fact]
    public async Task Save_FromParallelStores_NeverCollidesOnTheTempFile()
    {
        var tmp = NewTemp();
        try
        {
            var path = Path.Combine(tmp, "a.json");
            var stores = new[] { new PluginApprovalStore(path), new PluginApprovalStore(path) };

            await Task.WhenAll(Enumerable.Range(0, 50).Select(i => Task.Run(() =>
                stores[i % 2].Set($"com.example.p{i}", "h", SignatureStatus.Unsigned, ApprovalState.Enabled, Folder))));

            Assert.Equal(["a.json"], Directory.GetFiles(tmp).Select(Path.GetFileName));
            // The file is whole: it parses and holds records (the two stores may overwrite each other's additions).
            var final = new PluginApprovalStore(path);
            Assert.Contains(Enumerable.Range(0, 50), i => final.StoredState($"com.example.p{i}", Folder) == ApprovalState.Enabled);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }
}
