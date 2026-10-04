using System.Security.Cryptography;
using System.Text;
using nexIRC.Application;
using nexIRC.Core.Session;

namespace nexIRC.Application.Tests;

public sealed class Phase52ConversationDraftStorageTests
{
    [Fact]
    public void ProtectedDraftSnapshotRestoresUnsyncedConflictAndPendingMutationAfterRestart()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "conversation-drafts.json");
        var expected = new LocalConversationDraft(
            Guid.Parse("a0cb6d78-c0e0-45e7-a034-191e8acafc6c"),
            "channel:#alpha",
            "local conflicting draft\nwith Unicode: λ",
            BaseRevision: 5,
            ServerText: "authoritative server draft",
            ServerRevision: 6,
            HasConflict: true,
            PendingMutationId: "0123456789abcdef0123456789abcdef",
            PendingBaseRevision: 5,
            PendingText: "local conflicting draft\nwith Unicode: λ",
            HasLocalChanges: true);

        var firstProcessStore = new ProtectedJsonConversationDraftStore(path, new TestProtector());
        Assert.True(firstProcessStore.Save([expected]));

        // A new store instance represents a client process restart.
        var restartedStore = new ProtectedJsonConversationDraftStore(path, new TestProtector());
        Assert.Equal(expected, Assert.Single(restartedStore.Load()));
        Assert.NotEqual(Encoding.UTF8.GetString(File.ReadAllBytes(path)), expected.Text);
    }

    [Fact]
    public void ProtectedDraftSnapshotRetainsLocallyAvailableTextAboveSyncByteLimit()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "conversation-drafts.json");
        var text = new string('x', 4097);
        var expected = new LocalConversationDraft(Guid.NewGuid(), "channel:#large-local", text, 0,
            string.Empty, 0, false, HasLocalChanges: true);
        var firstProcessStore = new ProtectedJsonConversationDraftStore(path, new TestProtector());

        Assert.False(nexIRC.Core.Protocol.NexIrcDraftStateProtocol.TryEncodePayload(text, out _));
        Assert.True(firstProcessStore.Save([expected]));

        var restartedStore = new ProtectedJsonConversationDraftStore(path, new TestProtector());
        Assert.Equal(expected, Assert.Single(restartedStore.Load()));
    }

    [Fact]
    public void CorruptOrOversizedDraftSnapshotDoesNotFabricateText()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "conversation-drafts.json");
        File.WriteAllText(path, "not-json");

        var store = new ProtectedJsonConversationDraftStore(path, new TestProtector());
        Assert.Empty(store.Load());
        Assert.False(store.Save([new LocalConversationDraft(
            Guid.NewGuid(), "channel:#alpha", new string('界', 21_846), 0, string.Empty, 0, false)]));
    }

    [Fact]
    public void DraftSnapshotRejectsDuplicateAndInvalidConversationRecords()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "conversation-drafts.json");
        var store = new ProtectedJsonConversationDraftStore(path, new TestProtector());
        var profileId = Guid.NewGuid();
        var first = new LocalConversationDraft(profileId, "channel:#alpha", "one", 0, string.Empty, 0, false);
        var duplicate = first with { Text = "two" };

        Assert.False(store.Save([first, duplicate]));
        Assert.False(store.Save([first with { PendingMutationId = "bearer-token" }]));
        Assert.False(store.Save([first with { BaseRevision = -1 }]));
    }

    private sealed class TestProtector : IResumeSecretProtector
    {
        public byte[] Protect(string secret, string protectionContext)
        {
            var clear = Encoding.UTF8.GetBytes(secret);
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(protectionContext));
            for (var index = 0; index < clear.Length; index++) clear[index] ^= key[index % key.Length];
            return [0x52, 0x52, .. clear];
        }

        public string Unprotect(ReadOnlySpan<byte> protectedSecret, string protectionContext)
        {
            if (protectedSecret.Length < 2 || protectedSecret[0] != 0x52 || protectedSecret[1] != 0x52)
                throw new CryptographicException("The protected test value is malformed.");
            var value = protectedSecret[2..].ToArray();
            var key = SHA256.HashData(Encoding.UTF8.GetBytes(protectionContext));
            for (var index = 0; index < value.Length; index++) value[index] ^= key[index % key.Length];
            return Encoding.UTF8.GetString(value);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexirc-phase52-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
