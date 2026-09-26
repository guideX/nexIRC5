using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using nexIRC.Application;
using nexIRC.Core.Session;

namespace nexIRC.Application.Tests;

public sealed class Phase37ResumeStateStoreTests
{
    [Fact]
    public async Task JsonStoreRoundTripsProtectedMetadataWithoutBearerText()
    {
        var root = Directory.CreateTempSubdirectory("nexirc-phase37-");
        try
        {
            using var store = new JsonResumeStateStore(root.FullName);
            var state = CreateState("phase37-bearer-token");

            var save = await store.SaveAsync(state);
            var loaded = await store.LoadAsync(state.NetworkIdentity);
            var secret = new TestProtector().Unprotect(loaded.State!.ProtectedCurrentToken, state.NetworkIdentity);

            Assert.Equal(ResumeStateStoreStatus.Stored, save.Status);
            Assert.True(loaded.IsUsable);
            Assert.Equal("phase37-bearer-token", secret);
            Assert.DoesNotContain("phase37-bearer-token", File.ReadAllText(Directory.EnumerateFiles(root.FullName, "*.json").Single()), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptPrimaryFallsBackToAtomicBackupAndUnsupportedStateIsRejected()
    {
        var root = Directory.CreateTempSubdirectory("nexirc-phase37-");
        try
        {
            using var store = new JsonResumeStateStore(root.FullName);
            var first = CreateState("first-token");
            var second = first with
            {
                ProtectedCurrentToken = new TestProtector().Protect("second-token", first.NetworkIdentity),
                UpdatedAt = first.UpdatedAt.AddSeconds(1)
            };
            await store.SaveAsync(first);
            await store.SaveAsync(second);
            var path = Directory.EnumerateFiles(root.FullName, "*.json").Single();
            File.WriteAllText(path, "not-json");

            var recovered = await store.LoadAsync(first.NetworkIdentity);

            Assert.True(recovered.IsUsable);
            Assert.Equal(first.UpdatedAt, recovered.State!.UpdatedAt);
            Assert.Equal(ResumeStateLoadStatus.Loaded, recovered.Status);

            File.WriteAllText(path, JsonSerializer.Serialize(second with { Version = 99 }));
            File.Delete(path + ".bak");
            var unsupported = await store.LoadAsync(first.NetworkIdentity);
            Assert.Equal(ResumeStateLoadStatus.Unsupported, unsupported.Status);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public void WindowsDpapiBindsProtectedSecretToTheUserAndNetworkContext()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var protector = new WindowsDpapiResumeSecretProtector();
        var protectedSecret = protector.Protect("phase37-dpapi-secret", "profile=one;host=example.test");

        Assert.DoesNotContain("phase37-dpapi-secret", Convert.ToBase64String(protectedSecret), StringComparison.Ordinal);
        Assert.Equal("phase37-dpapi-secret", protector.Unprotect(protectedSecret, "profile=one;host=example.test"));
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(protectedSecret, "profile=two;host=example.test"));
    }

    private static ClientResumeStateRecord CreateState(string token)
    {
        var identity = "profile=phase37;host=example.test;port=6697;tls=true";
        var protector = new TestProtector();
        return new ClientResumeStateRecord(
            ClientResumeStateRecord.CurrentVersion,
            identity,
            "alice",
            "1",
            protector.Protect(token, identity),
            null,
            1,
            null,
            1,
            "s1",
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
    }

    private sealed class TestProtector : IResumeSecretProtector
    {
        public byte[] Protect(string secret, string protectionContext)
        {
            var clear = Encoding.UTF8.GetBytes(secret);
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(protectionContext));
            for (var index = 0; index < clear.Length; index++)
            {
                clear[index] ^= key[index % key.Length];
            }

            return [0x50, 0x37, .. clear];
        }

        public string Unprotect(ReadOnlySpan<byte> protectedSecret, string protectionContext)
        {
            if (protectedSecret.Length < 2 || protectedSecret[0] != 0x50 || protectedSecret[1] != 0x37)
            {
                throw new CryptographicException("The test protected value was malformed.");
            }

            var value = protectedSecret[2..].ToArray();
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(protectionContext));
            for (var index = 0; index < value.Length; index++)
            {
                value[index] ^= key[index % key.Length];
            }

            return Encoding.UTF8.GetString(value);
        }
    }
}
