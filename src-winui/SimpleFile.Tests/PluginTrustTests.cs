using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SimpleFile.PluginHost.Manifest;
using SimpleFile.PluginHost.Trust;
using Xunit;

namespace SimpleFile.Tests;

// Ed25519Verify is a process-wide static seam, so tests that touch it must not run in parallel
// with each other. They all live in this one class, which xunit runs serially.
public class PluginTrustTests
{
    private const string Id = "com.example.a";

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

    [Fact]
    public void Verify_DefaultPrimitive_IsInvalidWithUnavailableMessage()
    {
        var tmp = NewTemp();
        try
        {
            var dir = Package(tmp, out var files);
            WriteSignature(dir, files, Sig);
            var state = PluginSignatureVerifier.Verify(dir, Manifest(Convert.ToBase64String(Key)));
            Assert.Equal(SignatureStatus.Invalid, state.Status);
            Assert.Equal("Ed25519 verification is unavailable in this build.", state.Error);
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
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

    [Fact]
    public void Verify_TamperedManifest_IsInvalid()
    {
        // The manifest only enters the signature through the canonical payload, so the primitive
        // is what rejects it. This delegate stands in for a real verifier that signed the original.
        var tmp = NewTemp();
        var original = PluginSignatureVerifier.Ed25519Verify;
        try
        {
            var dir = Package(tmp, out var files);
            WriteSignature(dir, files, Sig);
            var signedPayload = PluginSignatureVerifier.CanonicalPayload(dir, files);
            File.WriteAllText(Path.Combine(dir, "plugin.json"), "{\"id\":\"" + Id + "\",\"evil\":true}");
            PluginSignatureVerifier.Ed25519Verify = (_, p, _) => p.AsSpan().SequenceEqual(signedPayload);

            var state = PluginSignatureVerifier.Verify(dir, Manifest(Convert.ToBase64String(Key)));

            Assert.Equal(SignatureStatus.Invalid, state.Status);
            Assert.NotNull(state.Error);
        }
        finally
        {
            PluginSignatureVerifier.Ed25519Verify = original;
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        }
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
            Assert.Equal(ApprovalState.NeedsConsent, store.Resolve(Id, "h", SignatureStatus.Unsigned));
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
            store.Set(Id, "h", SignatureStatus.Unsigned, ApprovalState.Enabled);
            Assert.Equal(ApprovalState.Enabled, store.Resolve(Id, "h", SignatureStatus.Unsigned));
            store.Set(Id, "h", SignatureStatus.Unsigned, ApprovalState.Disabled);
            Assert.Equal(ApprovalState.Disabled, store.Resolve(Id, "h", SignatureStatus.Unsigned));
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
            store.Set(Id, "h", SignatureStatus.Unsigned, ApprovalState.Enabled);
            Assert.Equal(ApprovalState.NeedsConsent, store.Resolve(Id, "other", SignatureStatus.Unsigned));
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
            store.Set(Id, "h", SignatureStatus.Unsigned, ApprovalState.Enabled);
            Assert.Equal(ApprovalState.NeedsConsent, store.Resolve(Id, "h", SignatureStatus.Verified));
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
            new PluginApprovalStore(path).Set(Id, "h", SignatureStatus.Verified, ApprovalState.Enabled);
            var again = new PluginApprovalStore(path);
            Assert.Equal(ApprovalState.Enabled, again.Resolve(Id, "h", SignatureStatus.Verified));
            again.Remove(Id);
            Assert.Equal(ApprovalState.NeedsConsent, new PluginApprovalStore(path).Resolve(Id, "h", SignatureStatus.Verified));
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
            Assert.Equal(ApprovalState.NeedsConsent, new PluginApprovalStore(path).Resolve(Id, "h", SignatureStatus.Unsigned));
        }
        finally { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); }
    }
}
