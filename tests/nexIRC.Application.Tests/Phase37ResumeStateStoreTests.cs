using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using nexIRC.Application;
using nexIRC.Core.Session;

namespace nexIRC.Application.Tests;

public sealed class Phase37ResumeStateStoreTests
{
    public static IEnumerable<object[]> PreCommitFailureStages =>
    [
        [(int)ResumeStatePersistenceStage.BeforeSerialization],
        [(int)ResumeStatePersistenceStage.AfterSerialization],
        [(int)ResumeStatePersistenceStage.AfterTemporaryFileCreated],
        [(int)ResumeStatePersistenceStage.AfterWrite],
        [(int)ResumeStatePersistenceStage.AfterWriteThroughFlush],
        [(int)ResumeStatePersistenceStage.BeforeAtomicReplacement]
    ];

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
    public async Task VersionOneStateMigratesExplicitlyAndPreservesItsPrimaryAttachmentToken()
    {
        var root = Directory.CreateTempSubdirectory("nexirc-phase40-v1-migration-");
        try
        {
            var state = CreateState("phase40-legacy-token");
            using var store = new JsonResumeStateStore(root.FullName);
            Assert.Equal(ResumeStateStoreStatus.Stored, (await store.SaveAsync(state)).Status);
            var path = Directory.EnumerateFiles(root.FullName, "*.json").Single();
            var legacyJson = JsonSerializer.Serialize(new
            {
                Version = 1,
                state.NetworkIdentity,
                state.AccountIdentity,
                state.ProtocolVersion,
                state.ProtectedCurrentToken,
                state.ProtectedPendingToken,
                state.TokenGeneration,
                state.PendingTokenGeneration,
                state.AcknowledgedTokenGeneration,
                state.AuthoritativeBoundary,
                state.CreatedAt,
                state.UpdatedAt,
                state.ExpiresAt,
                state.ServerGeneration
            });
            File.WriteAllText(path, legacyJson);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");

            var loaded = await store.LoadAsync(state.NetworkIdentity);

            Assert.True(loaded.IsUsable);
            Assert.Equal(ClientResumeStateRecord.CurrentVersion, loaded.State!.Version);
            Assert.Null(loaded.State.AttachmentId);
            Assert.Null(loaded.State.ProtectedSessionCredential);
            Assert.Equal("phase40-legacy-token", new TestProtector().Unprotect(loaded.State.ProtectedCurrentToken, state.NetworkIdentity));
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task VersionOneStateWithAttachmentFieldsIsRejectedAsCorrupt()
    {
        var root = Directory.CreateTempSubdirectory("nexirc-phase40-v1-malformed-");
        try
        {
            var state = CreateState("phase40-legacy-token");
            using var store = new JsonResumeStateStore(root.FullName);
            Assert.Equal(ResumeStateStoreStatus.Stored, (await store.SaveAsync(state)).Status);
            var path = Directory.EnumerateFiles(root.FullName, "*.json").Single();
            var malformed = JsonSerializer.Serialize(state with
            {
                Version = 1,
                AttachmentId = Guid.NewGuid(),
                ProtectedSessionCredential = new TestProtector().Protect("unexpected-grant", state.NetworkIdentity)
            });
            File.WriteAllText(path, malformed);
            if (File.Exists(path + ".bak")) File.Delete(path + ".bak");

            var loaded = await store.LoadAsync(state.NetworkIdentity);

            Assert.Equal(ResumeStateLoadStatus.Corrupt, loaded.Status);
            Assert.Null(loaded.State);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task VersionTwoAttachmentAndSessionGrantRoundTripOnlyAsProtectedBytes()
    {
        var root = Directory.CreateTempSubdirectory("nexirc-phase40-v2-state-");
        try
        {
            var state = CreateState("phase40-device-token") with
            {
                AttachmentId = Guid.Parse("b667e890-020f-4226-a6c5-d14e7fb8c6c0"),
                ProtectedSessionCredential = new TestProtector().Protect("phase40-session-grant", "profile=phase37;host=example.test;port=6697;tls=true")
            };
            using var store = new JsonResumeStateStore(root.FullName);
            Assert.Equal(ResumeStateStoreStatus.Stored, (await store.SaveAsync(state)).Status);
            var loaded = await store.LoadAsync(state.NetworkIdentity);

            Assert.True(loaded.IsUsable);
            Assert.Equal(state.AttachmentId, loaded.State!.AttachmentId);
            Assert.Equal("phase40-session-grant", new TestProtector().Unprotect(loaded.State.ProtectedSessionCredential!, state.NetworkIdentity));
            var json = File.ReadAllText(Directory.EnumerateFiles(root.FullName, "*.json").Single());
            Assert.DoesNotContain("phase40-device-token", json, StringComparison.Ordinal);
            Assert.DoesNotContain("phase40-session-grant", json, StringComparison.Ordinal);
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

    [Theory]
    [MemberData(nameof(PreCommitFailureStages))]
    public async Task PreCommitStorageFaultRetainsAuthoritativePrimaryAndCleansTemporaryFile(int failureStageValue)
    {
        var failureStage = (ResumeStatePersistenceStage)failureStageValue;
        var root = Directory.CreateTempSubdirectory("nexirc-phase38-fault-");
        try
        {
            var first = CreateState("first-token");
            var replacement = first with
            {
                ProtectedCurrentToken = new TestProtector().Protect("second-token", first.NetworkIdentity),
                UpdatedAt = first.UpdatedAt.AddSeconds(1)
            };

            using (var seedStore = new JsonResumeStateStore(root.FullName))
            {
                Assert.Equal(ResumeStateStoreStatus.Stored, (await seedStore.SaveAsync(first)).Status);
            }

            using (var failingStore = new JsonResumeStateStore(root.FullName, observation =>
            {
                if (observation.Stage == failureStage)
                {
                    throw new IOException("Injected pre-commit storage failure.");
                }
            }))
            {
                var result = await failingStore.SaveAsync(replacement);
                Assert.Equal(ResumeStateStoreStatus.Failed, result.Status);
            }

            using var restartedStore = new JsonResumeStateStore(root.FullName);
            var loaded = await restartedStore.LoadAsync(first.NetworkIdentity);
            Assert.True(loaded.IsUsable);
            Assert.Equal(first.UpdatedAt, loaded.State!.UpdatedAt);
            Assert.Equal("first-token", new TestProtector().Unprotect(loaded.State.ProtectedCurrentToken, first.NetworkIdentity));
            Assert.Empty(Directory.EnumerateFiles(root.FullName, "*.tmp"));
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryCleanupFailureAfterReplacementKeepsNewPrimaryAndOneBackup()
    {
        var root = Directory.CreateTempSubdirectory("nexirc-phase38-cleanup-");
        try
        {
            var first = CreateState("first-token");
            var second = first with
            {
                ProtectedCurrentToken = new TestProtector().Protect("second-token", first.NetworkIdentity),
                UpdatedAt = first.UpdatedAt.AddSeconds(1)
            };

            using (var store = new JsonResumeStateStore(root.FullName))
            {
                Assert.Equal(ResumeStateStoreStatus.Stored, (await store.SaveAsync(first)).Status);
            }

            using (var store = new JsonResumeStateStore(root.FullName, observation =>
            {
                if (observation.Stage == ResumeStatePersistenceStage.BeforeTemporaryCleanup)
                {
                    throw new IOException("Injected cleanup failure.");
                }
            }))
            {
                Assert.Equal(ResumeStateStoreStatus.Stored, (await store.SaveAsync(second)).Status);
            }

            var jsonFiles = Directory.EnumerateFiles(root.FullName, "*.json").ToArray();
            Assert.Single(jsonFiles);
            Assert.True(File.Exists(jsonFiles[0] + ".bak"));
            Assert.Empty(Directory.EnumerateFiles(root.FullName, "*.tmp"));

            using var restartedStore = new JsonResumeStateStore(root.FullName);
            var loaded = await restartedStore.LoadAsync(first.NetworkIdentity);
            Assert.True(loaded.IsUsable);
            Assert.Equal(second.UpdatedAt, loaded.State!.UpdatedAt);
            Assert.Equal("second-token", new TestProtector().Unprotect(loaded.State.ProtectedCurrentToken, first.NetworkIdentity));
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Theory]
    [InlineData("truncated", ResumeStateLoadStatus.Corrupt)]
    [InlineData("malformed", ResumeStateLoadStatus.Corrupt)]
    [InlineData("unsupported", ResumeStateLoadStatus.Unsupported)]
    [InlineData("missing-current-token", ResumeStateLoadStatus.Corrupt)]
    [InlineData("pending-generation-before-current", ResumeStateLoadStatus.Corrupt)]
    [InlineData("acknowledgement-after-current", ResumeStateLoadStatus.Corrupt)]
    [InlineData("malformed-boundary", ResumeStateLoadStatus.Corrupt)]
    [InlineData("mismatched-network", ResumeStateLoadStatus.Corrupt)]
    public async Task CorruptionMatrixReturnsBoundedFallbackStatus(string corruption, ResumeStateLoadStatus expectedStatus)
    {
        var root = Directory.CreateTempSubdirectory("nexirc-phase38-corrupt-");
        try
        {
            var state = CreateState("corruption-token");
            using (var seedStore = new JsonResumeStateStore(root.FullName))
            {
                Assert.Equal(ResumeStateStoreStatus.Stored, (await seedStore.SaveAsync(state)).Status);
            }

            var path = Directory.EnumerateFiles(root.FullName, "*.json").Single();
            var contents = corruption switch
            {
                "truncated" => "{\"Version\":1",
                "malformed" => "not-json",
                "unsupported" => JsonSerializer.Serialize(state with { Version = 99 }),
                "missing-current-token" => JsonSerializer.Serialize(state with { ProtectedCurrentToken = [] }),
                "pending-generation-before-current" => JsonSerializer.Serialize(state with
                {
                    ProtectedPendingToken = [1, 2, 3],
                    PendingTokenGeneration = 1
                }),
                "acknowledgement-after-current" => JsonSerializer.Serialize(state with { AcknowledgedTokenGeneration = state.TokenGeneration + 1 }),
                "malformed-boundary" => JsonSerializer.Serialize(state with { AuthoritativeBoundary = " " }),
                "mismatched-network" => JsonSerializer.Serialize(state with { NetworkIdentity = "another-network" }),
                _ => throw new ArgumentOutOfRangeException(nameof(corruption))
            };
            File.WriteAllText(path, contents);

            using var store = new JsonResumeStateStore(root.FullName);
            var loaded = await store.LoadAsync(state.NetworkIdentity);

            Assert.Equal(expectedStatus, loaded.Status);
            Assert.DoesNotContain("corruption-token", loaded.Detail ?? string.Empty, StringComparison.Ordinal);
            if (expectedStatus == ResumeStateLoadStatus.Corrupt)
            {
                Assert.True(File.Exists(path + ".bad"));
            }
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptPrimaryAndBackupReturnCorruptWithoutSelectingEitherRecord()
    {
        var root = Directory.CreateTempSubdirectory("nexirc-phase38-corrupt-pair-");
        try
        {
            using (var store = new JsonResumeStateStore(root.FullName))
            {
                var first = CreateState("first-token");
                var second = first with { UpdatedAt = first.UpdatedAt.AddSeconds(1) };
                Assert.Equal(ResumeStateStoreStatus.Stored, (await store.SaveAsync(first)).Status);
                Assert.Equal(ResumeStateStoreStatus.Stored, (await store.SaveAsync(second)).Status);
            }

            var primary = Directory.EnumerateFiles(root.FullName, "*.json").Single();
            File.WriteAllText(primary, "broken-primary");
            File.WriteAllText(primary + ".bak", "broken-backup");

            using var restartedStore = new JsonResumeStateStore(root.FullName);
            var loaded = await restartedStore.LoadAsync(CreateState("first-token").NetworkIdentity);

            Assert.Equal(ResumeStateLoadStatus.Corrupt, loaded.Status);
            Assert.True(File.Exists(primary + ".bad"));
            Assert.True(File.Exists(primary + ".bak"));
            Assert.Null(loaded.State);
        }
        finally
        {
            Directory.Delete(root.FullName, recursive: true);
        }
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
