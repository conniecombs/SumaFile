using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using SimpleFile.PluginHost.Manifest;
using SimpleFile.PluginHost.Trust;
using Xunit;

namespace SimpleFile.Tests;

// Ed25519Verify is a process-wide static seam, so tests that touch it must not run in parallel
// with each other. They all live in this one class, which xunit runs serially.
public class PluginTrustTests
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
    public void ApprovalStore_DefaultPath_HonorsEnvOverrideOnlyWhenSet()
    {
        var localDefault = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SumaFile", "plugin-approvals.json");
        var previous = Environment.GetEnvironmentVariable(PluginApprovalStore.PathVariable);
        try
        {
            Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, null);
            Assert.Equal(localDefault, PluginApprovalStore.DefaultPath());
            Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, "  ");
            Assert.Equal(localDefault, PluginApprovalStore.DefaultPath());
            Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, @" C:\smoke\approvals.json ");
            Assert.Equal(@"C:\smoke\approvals.json", PluginApprovalStore.DefaultPath());
        }
        finally
        {
            Environment.SetEnvironmentVariable(PluginApprovalStore.PathVariable, previous);
        }
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
}
