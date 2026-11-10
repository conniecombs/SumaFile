using SumaFile.Remote.Model;
using Xunit;

namespace SimpleFile.Tests.Remote;

public class RemoteManagerViewModelTests
{
    private static RemoteManagerViewModel Create(FakeRemoteBackend backend) => new(backend, backend);

    [Fact]
    public async Task LoadProfilesAsync_SelectsFirstProfileAndEnablesActions()
    {
        var backend = new FakeRemoteBackend(
        [
            new RemoteProfile
            {
                Id = "prod",
                Name = "Production",
                Protocol = "sftp",
                Host = "example.com",
                Port = 22,
                Username = "deploy",
                RootPath = "/var/www",
                AuthKind = "password",
            },
        ]);
        var viewModel = Create(backend);

        Assert.False(viewModel.CanConnect);

        await viewModel.LoadProfilesAsync();

        var profile = Assert.Single(viewModel.Profiles);
        Assert.Same(profile, viewModel.SelectedProfile);
        Assert.Equal("Production", profile.Name);
        Assert.Equal("Production - sftp://deploy@example.com:22/var/www", viewModel.SelectedProfileSummary);
        Assert.Equal("1 remote profile", viewModel.StatusText);
        Assert.True(viewModel.CanConnect);
        Assert.True(viewModel.CanDeleteProfile);
        Assert.Equal(1, backend.ListCalls);
    }

    [Fact]
    public async Task LoadProfilesAsync_WhenEmptyShowsEmptyStateAndDisablesActions()
    {
        var viewModel = Create(new FakeRemoteBackend([]));

        await viewModel.LoadProfilesAsync();

        Assert.Empty(viewModel.Profiles);
        Assert.Null(viewModel.SelectedProfile);
        Assert.Equal("No remote profiles", viewModel.StatusText);
        Assert.False(viewModel.CanConnect);
        Assert.False(viewModel.CanDeleteProfile);
    }

    [Fact]
    public async Task LoadProfilesAsync_SelectsRequestedProfileWhenOpenedFromSidebar()
    {
        var viewModel = Create(new FakeRemoteBackend(
        [
            new RemoteProfile
            {
                Id = "staging",
                Name = "Staging",
                Protocol = "ftp",
                Host = "staging.example.com",
                Port = 21,
                Username = "web",
                RootPath = "/incoming",
                AuthKind = "password",
            },
            new RemoteProfile
            {
                Id = "prod",
                Name = "Production",
                Protocol = "sftp",
                Host = "example.com",
                Port = 22,
                Username = "deploy",
                RootPath = "/var/www",
                AuthKind = "password",
            },
        ]));

        viewModel.RequestProfileSelection("prod");
        await viewModel.LoadProfilesAsync();

        Assert.Equal("prod", viewModel.SelectedProfile?.Id);
        Assert.Equal("Production", viewModel.SelectedProfile?.Name);
        Assert.Equal("Production - sftp://deploy@example.com:22/var/www", viewModel.SelectedProfileSummary);
    }

    [Fact]
    public async Task ConnectSelectedProfileAsync_ConnectsAndLoadsRemoteListing()
    {
        var backend = new FakeRemoteBackend(
        [
            new RemoteProfile
            {
                Id = "prod",
                Name = "Production",
                Protocol = "sftp",
                Host = "example.com",
                Port = 22,
                Username = "deploy",
                RootPath = "/",
                AuthKind = "password",
            },
        ])
        {
            Session = new RemoteSession
            {
                SessionId = "remote-session-1",
                ProfileId = "prod",
                Protocol = "sftp",
                RootPath = "/",
            },
            Listing = new RemoteDirectoryListing
            {
                Path = "/",
                Parent = null,
                IsNetwork = true,
                Entries =
                [
                    new RemoteFileEntry
                    {
                        Name = "release",
                        Path = "/release",
                        IsDir = true,
                        Modified = "2026-09-21T00:00:00Z",
                    },
                ],
            },
        };
        var viewModel = Create(backend);
        await viewModel.LoadProfilesAsync();

        await viewModel.ConnectSelectedProfileAsync("not-returned");

        Assert.Equal("prod", backend.ConnectedProfileId);
        Assert.Equal("not-returned", backend.ConnectedSecret);
        Assert.True(viewModel.IsConnected);
        Assert.False(viewModel.CanConnect);
        Assert.True(viewModel.CanDisconnect);
        Assert.Equal("Connected to Production", viewModel.StatusText);
        Assert.Equal("/", viewModel.RemotePath);
        Assert.Equal("release", Assert.Single(viewModel.RemoteEntries).Name);
    }

    [Fact]
    public void ParseConnectError_ReadsUntrustedFingerprintAfterTheLastSeparator()
    {
        var outcome = RemoteManagerViewModel.ParseConnectError(
            "SFTP host fingerprint is not trusted. Check that this fingerprint belongs to the server, then trust it to connect: SHA256:abc+def/123=");

        Assert.False(outcome.Connected);
        Assert.True(outcome.IsHostKeyProblem);
        Assert.Equal("SHA256:abc+def/123=", outcome.UntrustedFingerprint);
        Assert.Null(outcome.MismatchExpected);
        Assert.Null(outcome.MismatchActual);
    }

    [Fact]
    public void ParseConnectError_ReadsExpectedAndReceivedFingerprintsOnMismatch()
    {
        var outcome = RemoteManagerViewModel.ParseConnectError(
            "SFTP host fingerprint mismatch. The server key changed since you trusted it. Expected SHA256:old, got SHA256:new");

        Assert.True(outcome.IsHostKeyProblem);
        Assert.Null(outcome.UntrustedFingerprint);
        Assert.Equal("SHA256:old", outcome.MismatchExpected);
        Assert.Equal("SHA256:new", outcome.MismatchActual);
    }

    [Theory]
    [InlineData("SFTP connection failed: connection refused")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseConnectError_LeavesOtherFailuresAlone(string? message)
    {
        var outcome = RemoteManagerViewModel.ParseConnectError(message);

        Assert.False(outcome.IsHostKeyProblem);
        Assert.Null(outcome.UntrustedFingerprint);
        Assert.Null(outcome.MismatchActual);
    }

    [Fact]
    public async Task ConnectSelectedProfileAsync_ReturnsUntrustedFingerprintAndTrustReconnects()
    {
        var backend = new FakeRemoteBackend(
        [
            new RemoteProfile
            {
                Id = "prod",
                Name = "Production",
                Protocol = "sftp",
                Host = "example.com",
                Port = 22,
                Username = "deploy",
                RootPath = "/",
                AuthKind = "password",
                CredentialTarget = "SumaFile:remote:prod",
            },
        ])
        {
            Session = new RemoteSession { SessionId = "s1", ProfileId = "prod", Protocol = "sftp", RootPath = "/" },
            Listing = new RemoteDirectoryListing { Path = "/", IsNetwork = true, Entries = [] },
        };
        backend.ConnectFailures.Enqueue("SFTP host fingerprint is not trusted. Trust it to connect: SHA256:xyz");
        var viewModel = Create(backend);
        await viewModel.LoadProfilesAsync();

        var first = await viewModel.ConnectSelectedProfileAsync();

        Assert.False(first.Connected);
        Assert.Equal("SHA256:xyz", first.UntrustedFingerprint);
        Assert.False(viewModel.IsConnected);
        Assert.False(viewModel.IsBusy);

        var second = await viewModel.TrustFingerprintAndConnectAsync(first.UntrustedFingerprint!);

        Assert.True(second.Connected);
        Assert.Equal("SHA256:xyz", backend.SavedProfile?.TrustedHostFingerprint);
        Assert.Equal("prod", backend.SavedProfile?.Id);
        Assert.Equal("SumaFile:remote:prod", backend.SavedProfile?.CredentialTarget);
        Assert.Null(backend.SavedSecret);
        Assert.Equal(2, backend.ConnectCalls);
        Assert.True(viewModel.IsConnected);
    }

    [Fact]
    public async Task ConnectSelectedProfileAsync_StillThrowsForOrdinaryFailures()
    {
        var backend = new FakeRemoteBackend(
        [
            new RemoteProfile { Id = "prod", Name = "Production", Protocol = "sftp", Host = "example.com", Port = 22 },
        ]);
        backend.ConnectFailures.Enqueue("SFTP connection failed: connection refused");
        var viewModel = Create(backend);
        await viewModel.LoadProfilesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => viewModel.ConnectSelectedProfileAsync());
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task RemoteMutationMethods_UpdateDirectoryEntriesAndStatus()
    {
        var backend = new FakeRemoteBackend(
        [
            new RemoteProfile
            {
                Id = "prod",
                Name = "Production",
                Protocol = "sftp",
                Host = "example.com",
                Port = 22,
                Username = "deploy",
                RootPath = "/",
                AuthKind = "password",
            },
        ])
        {
            Session = new RemoteSession
            {
                SessionId = "remote-session-1",
                ProfileId = "prod",
                Protocol = "sftp",
                RootPath = "/",
            },
            Listing = new RemoteDirectoryListing
            {
                Path = "/",
                Parent = null,
                IsNetwork = true,
                Entries = [],
            },
        };
        var viewModel = Create(backend);
        await viewModel.LoadProfilesAsync();
        await viewModel.ConnectSelectedProfileAsync();

        await viewModel.CreateRemoteDirectoryAsync("incoming");

        Assert.Equal(("remote-session-1", "/", "incoming"), backend.CreatedDirectory);
        var created = Assert.Single(viewModel.RemoteEntries);
        Assert.Equal("incoming", created.Name);
        Assert.Equal("Created incoming", viewModel.StatusText);

        await viewModel.RenameRemoteEntryAsync(created, "archive");

        Assert.Equal(("remote-session-1", "/incoming", "archive"), backend.RenamedEntry);
        var renamed = Assert.Single(viewModel.RemoteEntries);
        Assert.Equal("archive", renamed.Name);
        Assert.Equal("/archive", renamed.Path);
        Assert.Equal("Renamed incoming to archive", viewModel.StatusText);

        await viewModel.DeleteRemoteEntriesAsync([renamed]);

        var deletedEntries = Assert.NotNull(backend.DeletedEntries);
        Assert.Equal("remote-session-1", deletedEntries.RemoteSessionId);
        Assert.Equal(["/archive"], deletedEntries.Paths);
        Assert.Empty(viewModel.RemoteEntries);
        Assert.Equal("Deleted 1 remote item", viewModel.StatusText);
    }

    [Fact]
    public async Task RemoteMutationMethods_KeepTheFolderOtherEntries()
    {
        // Found by the Task 15 UI smoke: each mutation rebuilt the list from a lazy query over the list it had just
        // cleared, so creating, renaming or deleting one entry dropped every other entry of the folder.
        var backend = new FakeRemoteBackend(
        [
            new RemoteProfile { Id = "prod", Name = "Production", Protocol = "sftp", Host = "example.com", Port = 22, RootPath = "/", AuthKind = "password" },
        ])
        {
            Session = new RemoteSession { SessionId = "remote-session-1", ProfileId = "prod", Protocol = "sftp", RootPath = "/" },
            Listing = new RemoteDirectoryListing
            {
                Path = "/",
                Parent = null,
                IsNetwork = true,
                Entries =
                [
                    new RemoteFileEntry { Name = "big.bin", Path = "/big.bin" },
                    new RemoteFileEntry { Name = "café 資料", Path = "/café 資料", IsDir = true },
                ],
            },
        };
        var viewModel = Create(backend);
        await viewModel.LoadProfilesAsync();
        await viewModel.ConnectSelectedProfileAsync();

        await viewModel.CreateRemoteDirectoryAsync("smoke-folder");
        Assert.Equal(["big.bin", "café 資料", "smoke-folder"], viewModel.RemoteEntries.Select(entry => entry.Name));

        await viewModel.RenameRemoteEntryAsync(viewModel.RemoteEntries.Single(entry => entry.Name == "smoke-folder"), "renamed folder [ok]");
        Assert.Equal(["big.bin", "café 資料", "renamed folder [ok]"], viewModel.RemoteEntries.Select(entry => entry.Name));

        await viewModel.DeleteRemoteEntriesAsync([viewModel.RemoteEntries.Single(entry => entry.Name == "renamed folder [ok]")]);
        Assert.Equal(["big.bin", "café 資料"], viewModel.RemoteEntries.Select(entry => entry.Name));
    }

    [Fact]
    public async Task RemoteTransferMethods_DownloadAndUploadFiles()
    {
        var backend = new FakeRemoteBackend(
        [
            new RemoteProfile
            {
                Id = "prod",
                Name = "Production",
                Protocol = "sftp",
                Host = "example.com",
                Port = 22,
                Username = "deploy",
                RootPath = "/",
                AuthKind = "password",
            },
        ])
        {
            Session = new RemoteSession
            {
                SessionId = "remote-session-1",
                ProfileId = "prod",
                Protocol = "sftp",
                RootPath = "/incoming",
            },
            Listing = new RemoteDirectoryListing
            {
                Path = "/incoming",
                Parent = null,
                IsNetwork = true,
                Entries =
                [
                    new RemoteFileEntry
                    {
                        Name = "app.txt",
                        Path = "/incoming/app.txt",
                        Modified = "2026-09-21T00:00:00Z",
                    },
                ],
            },
        };
        var viewModel = Create(backend);
        await viewModel.LoadProfilesAsync();
        await viewModel.ConnectSelectedProfileAsync();
        var remoteEntry = Assert.Single(viewModel.RemoteEntries);

        await viewModel.DownloadRemoteEntriesAsync([remoteEntry], @"C:\Downloads");

        Assert.Equal(("remote-session-1", "/incoming/app.txt", @"C:\Downloads\app.txt"), backend.DownloadedFile);
        Assert.StartsWith("Transfer finished: 1 files completed", viewModel.StatusText);
        var downloadRecord = Assert.Single(viewModel.Transfers);
        Assert.Equal("Download", downloadRecord.Direction);
        Assert.Equal("Completed", downloadRecord.Status);
        Assert.Equal("/incoming/app.txt", downloadRecord.RemotePath);
        Assert.Equal(@"C:\Downloads\app.txt", downloadRecord.LocalPath);

        await viewModel.UploadLocalFilesAsync([@"C:\Uploads\report.pdf"]);

        Assert.Equal(("remote-session-1", @"C:\Uploads\report.pdf", "/incoming/report.pdf"), backend.UploadedFile);
        Assert.Contains(viewModel.RemoteEntries, entry => entry.Name == "report.pdf");
        Assert.StartsWith("Transfer finished: 1 files completed", viewModel.StatusText);
        var uploadRecord = Assert.Single(viewModel.Transfers, transfer => transfer.Direction == "Upload");
        Assert.Equal("Completed", uploadRecord.Status);
        Assert.Equal("/incoming/report.pdf", uploadRecord.RemotePath);
        Assert.Equal(@"C:\Uploads\report.pdf", uploadRecord.LocalPath);
    }

    [Fact]
    public async Task RemoteNavigationMethods_BrowseFoldersAndParents()
    {
        var backend = new FakeRemoteBackend(
        [
            new RemoteProfile
            {
                Id = "prod",
                Name = "Production",
                Protocol = "sftp",
                Host = "example.com",
                Port = 22,
                Username = "deploy",
                RootPath = "/",
                AuthKind = "password",
            },
        ])
        {
            Session = new RemoteSession
            {
                SessionId = "remote-session-1",
                ProfileId = "prod",
                Protocol = "sftp",
                RootPath = "/",
            },
            ListingsByPath =
            {
                ["/"] = new RemoteDirectoryListing
                {
                    Path = "/",
                    Parent = null,
                    IsNetwork = true,
                    Entries =
                    [
                        new RemoteFileEntry
                        {
                            Name = "incoming",
                            Path = "/incoming",
                            IsDir = true,
                            Modified = "2026-09-21T00:00:00Z",
                        },
                    ],
                },
                ["/incoming"] = new RemoteDirectoryListing
                {
                    Path = "/incoming",
                    Parent = "/",
                    IsNetwork = true,
                    Entries =
                    [
                        new RemoteFileEntry
                        {
                            Name = "app.txt",
                            Path = "/incoming/app.txt",
                            Modified = "2026-09-21T00:00:00Z",
                        },
                    ],
                },
            },
        };
        var viewModel = Create(backend);
        await viewModel.LoadProfilesAsync();
        await viewModel.ConnectSelectedProfileAsync();

        var folder = Assert.Single(viewModel.RemoteEntries);
        await viewModel.NavigateRemoteEntryAsync(folder);

        Assert.Equal("/incoming", viewModel.RemotePath);
        Assert.Equal("app.txt", Assert.Single(viewModel.RemoteEntries).Name);

        await viewModel.GoToParentRemoteDirectoryAsync();

        Assert.Equal("/", viewModel.RemotePath);
        Assert.Equal(["/", "/incoming", "/"], backend.ListedPaths);
    }

    [Fact]
    public async Task LocalNavigationMethods_LoadBrowseAndReturnToParent()
    {
        var backend = new FakeRemoteBackend([])
        {
            LocalListingsByPath =
            {
                [@"C:\Work"] = new RemoteDirectoryListing
                {
                    Path = @"C:\Work",
                    Parent = @"C:\",
                    Entries =
                    [
                        new RemoteFileEntry
                        {
                            Name = "site",
                            Path = @"C:\Work\site",
                            IsDir = true,
                            Modified = "2026-09-21T00:00:00Z",
                        },
                    ],
                },
                [@"C:\Work\site"] = new RemoteDirectoryListing
                {
                    Path = @"C:\Work\site",
                    Parent = @"C:\Work",
                    Entries =
                    [
                        new RemoteFileEntry
                        {
                            Name = "index.html",
                            Path = @"C:\Work\site\index.html",
                            Modified = "2026-09-21T00:00:00Z",
                        },
                    ],
                },
                [@"C:\"] = new RemoteDirectoryListing
                {
                    Path = @"C:\",
                    Parent = null,
                    Entries = [],
                },
            },
        };
        var viewModel = Create(backend);

        await viewModel.NavigateLocalPathAsync(@"C:\Work");

        Assert.Equal(@"C:\Work", viewModel.LocalPath);
        Assert.Equal("site", Assert.Single(viewModel.LocalEntries).Name);
        Assert.Equal("Local path: C:\\Work", viewModel.StatusText);

        await viewModel.NavigateLocalEntryAsync(Assert.Single(viewModel.LocalEntries));

        Assert.Equal(@"C:\Work\site", viewModel.LocalPath);
        Assert.Equal("index.html", Assert.Single(viewModel.LocalEntries).Name);

        await viewModel.GoToParentLocalDirectoryAsync();

        Assert.Equal(@"C:\Work", viewModel.LocalPath);
        Assert.Equal([@"C:\Work", @"C:\Work\site", @"C:\Work"], backend.LocalListedPaths);
    }

    [Fact]
    public async Task WorkspaceTransferMethods_UseCurrentLocalAndRemoteFolders()
    {
        var backend = new FakeRemoteBackend(
        [
            new RemoteProfile
            {
                Id = "prod",
                Name = "Production",
                Protocol = "sftp",
                Host = "example.com",
                Port = 22,
                Username = "deploy",
                RootPath = "/incoming",
                AuthKind = "password",
            },
        ])
        {
            Session = new RemoteSession
            {
                SessionId = "remote-session-1",
                ProfileId = "prod",
                Protocol = "sftp",
                RootPath = "/incoming",
            },
            Listing = new RemoteDirectoryListing
            {
                Path = "/incoming",
                Parent = "/",
                IsNetwork = true,
                Entries =
                [
                    new RemoteFileEntry
                    {
                        Name = "server.log",
                        Path = "/incoming/server.log",
                        Modified = "2026-09-21T00:00:00Z",
                    },
                ],
            },
            LocalListingsByPath =
            {
                [@"C:\Deploy"] = new RemoteDirectoryListing
                {
                    Path = @"C:\Deploy",
                    Parent = @"C:\",
                    Entries =
                    [
                        new RemoteFileEntry
                        {
                            Name = "index.html",
                            Path = @"C:\Deploy\index.html",
                            Modified = "2026-09-21T00:00:00Z",
                        },
                    ],
                },
            },
        };
        var viewModel = Create(backend);
        await viewModel.LoadProfilesAsync();
        await viewModel.ConnectSelectedProfileAsync();
        await viewModel.NavigateLocalPathAsync(@"C:\Deploy");

        await viewModel.DownloadRemoteEntriesToLocalAsync(viewModel.RemoteEntries);

        Assert.Equal(("remote-session-1", "/incoming/server.log", @"C:\Deploy\server.log"), backend.DownloadedFile);
        Assert.StartsWith("Transfer finished: 1 files completed", viewModel.StatusText);

        await viewModel.UploadLocalEntriesToRemoteAsync(viewModel.LocalEntries);

        Assert.Equal(("remote-session-1", @"C:\Deploy\index.html", "/incoming/index.html"), backend.UploadedFile);
        Assert.Contains(viewModel.RemoteEntries, entry => entry.Name == "index.html");
        Assert.StartsWith("Transfer finished: 1 files completed", viewModel.StatusText);
    }


    [Fact]
    public async Task SaveProfileAsync_AddsSavedProfileAndSelectsIt()
    {
        var backend = new FakeRemoteBackend([]);
        var viewModel = Create(backend);
        await viewModel.LoadProfilesAsync();

        await viewModel.SaveProfileAsync(
            new RemoteProfileInput
            {
                Name = "Production",
                Protocol = "sftp",
                Host = "files.example.com",
                Port = 22,
                Username = "deploy",
                RootPath = "/var/www",
                AuthKind = "password",
                PassiveMode = true,
            },
            "saved-secret");

        var saved = Assert.Single(viewModel.Profiles);
        Assert.Equal("prod", saved.Id);
        Assert.Same(saved, viewModel.SelectedProfile);
        Assert.Equal("saved-secret", backend.SavedSecret);
        Assert.Equal("Saved Production", viewModel.StatusText);
        Assert.True(viewModel.CanConnect);
    }

    [Fact]
    public async Task DeleteSelectedProfileAsync_RemovesSelectedProfile()
    {
        var backend = new FakeRemoteBackend(
        [
            new RemoteProfile
            {
                Id = "prod",
                Name = "Production",
                Protocol = "sftp",
                Host = "example.com",
                Port = 22,
                Username = "deploy",
                RootPath = "/",
                AuthKind = "password",
            },
        ]);
        var viewModel = Create(backend);
        await viewModel.LoadProfilesAsync();

        await viewModel.DeleteSelectedProfileAsync();

        Assert.Equal("prod", backend.DeletedProfileId);
        Assert.Empty(viewModel.Profiles);
        Assert.Null(viewModel.SelectedProfile);
        Assert.Equal("Deleted Production", viewModel.StatusText);
        Assert.False(viewModel.CanConnect);
    }

    [Fact]
    public async Task TestProfileAsync_ReturnsResultAndUpdatesStatus()
    {
        var backend = new FakeRemoteBackend([])
        {
            TestResult = new RemoteConnectionTestResult
            {
                Ok = true,
                Message = "Profile settings are valid.",
                Capabilities = ["sftp"],
            },
        };
        var viewModel = Create(backend);

        var result = await viewModel.TestProfileAsync(
            new RemoteProfileInput
            {
                Name = "Production",
                Protocol = "sftp",
                Host = "files.example.com",
                Port = 22,
                Username = "deploy",
                RootPath = "/",
                AuthKind = "password",
                PassiveMode = true,
            },
            "saved-secret");

        Assert.True(result.Ok);
        Assert.Equal("saved-secret", backend.TestSecret);
        Assert.Equal("Profile settings are valid.", viewModel.StatusText);
    }

    [Fact]
    public async Task FolderSelectionsReachBackendAndRetryIncludesOnlyIncompleteSelections()
    {
        var backend = new FakeRemoteBackend([new RemoteProfile { Id = "test" }])
        {
            Session = new RemoteSession { SessionId = "session", ProfileId = "test", RootPath = "/" },
            Listing = new RemoteDirectoryListing { Path = "/" },
            TransferHandler = sources => Task.FromResult(new RemoteTransferOutcome
            {
                RetrySources = [sources[1]],
                Items = [new RemoteTransferItem { Source = sources[0], Destination = "/done", IsDir = true, Status = "completed" },
                    new RemoteTransferItem { Source = sources[1], Destination = "/failed", Status = "failed", Error = "Network interrupted" }],
            }),
        };
        var vm = Create(backend);
        vm.ConflictAction = "skip";
        await vm.LoadProfilesAsync(); await vm.ConnectSelectedProfileAsync();
        await vm.UploadLocalEntriesToRemoteAsync([new RemoteFileEntry { Path = @"C:\site", IsDir = true }, new RemoteFileEntry { Path = @"C:\other", IsDir = true }]);
        Assert.Equal([@"C:\site", @"C:\other"], backend.LastTransferSources);
        Assert.Equal("skip", backend.LastConflictAction);
        Assert.True(vm.CanRetryTransfers);
        Assert.Contains(vm.Transfers, record => record.Detail == "Network interrupted");
        await vm.DisconnectAsync();
        Assert.False(vm.CanRetryTransfers);
        backend.Session.SessionId = "reconnected-session";
        await vm.ConnectSelectedProfileAsync();
        Assert.True(vm.CanRetryTransfers);
        backend.TransferHandler = sources => Task.FromResult(new RemoteTransferOutcome());
        await vm.RetryTransfersAsync();
        Assert.Equal([@"C:\other"], backend.LastTransferSources);
        Assert.False(vm.CanRetryTransfers);
    }

    [Fact]
    public async Task CancelWaitsForBackendCleanupAndRetainsPartialResults()
    {
        var finished = new TaskCompletionSource<RemoteTransferOutcome>();
        var backend = new FakeRemoteBackend([new RemoteProfile { Id = "test" }])
        {
            Session = new RemoteSession { SessionId = "session", RootPath = "/" },
            Listing = new RemoteDirectoryListing { Path = "/" }, TransferHandler = _ => finished.Task,
        };
        var vm = Create(backend);
        await vm.LoadProfilesAsync(); await vm.ConnectSelectedProfileAsync();
        var running = vm.UploadLocalFilesAsync([@"C:\a.bin"]);
        Assert.True(vm.CanCancelTransfer);
        vm.CancelTransfer();
        Assert.NotNull(backend.CancelledOperation);
        Assert.True(vm.IsBusy);
        Assert.False(running.IsCompleted);
        finished.SetResult(new RemoteTransferOutcome { Cancelled = true, RetrySources = [@"C:\a.bin"],
            Items = [new RemoteTransferItem { Source = @"C:\a.bin", Destination = "/a.bin", Status = "cancelled" }] });
        await running;
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanRetryTransfers);
        Assert.Equal("Cancelled", Assert.Single(vm.Transfers).Status);
    }

    private const string BusyText = "Remote session is busy; wait for or cancel its transfer";

    [Fact]
    public async Task CloseSessionAsync_DuringATransfer_WaitsForItToEndThenDisconnects()
    {
        long transferEndedAt = 0;
        var transferRunning = false;
        var backend = new FakeRemoteBackend([new RemoteProfile { Id = "test" }])
        {
            Session = new RemoteSession { SessionId = "session", ProfileId = "test", RootPath = "/" },
            Listing = new RemoteDirectoryListing { Path = "/" },
        };
        // As the real backend: the session stays locked until the transfer has cleaned up, 300 ms after the cancel.
        backend.CancellableTransferHandler = async (_, ct) =>
        {
            Volatile.Write(ref transferRunning, true);
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            await Task.Delay(300, CancellationToken.None);
            Interlocked.Exchange(ref transferEndedAt, System.Diagnostics.Stopwatch.GetTimestamp());
            Volatile.Write(ref transferRunning, false);
            return new RemoteTransferOutcome { Cancelled = true };
        };
        backend.DisconnectHandler = _ =>
        {
            if (Volatile.Read(ref transferRunning))
            {
                throw new InvalidOperationException(BusyText);
            }
        };
        var vm = Create(backend);
        await vm.LoadProfilesAsync();
        await vm.ConnectSelectedProfileAsync();
        var running = vm.UploadLocalFilesAsync([@"C:\a.bin"]);
        Assert.True(vm.CanCancelTransfer);

        var closed = await vm.CloseSessionAsync(TimeSpan.FromSeconds(5));

        Assert.True(closed);
        await running.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("session", backend.DisconnectedSessionId);
        Assert.NotEqual(0, Interlocked.Read(ref transferEndedAt));
        Assert.True(backend.DisconnectAttempts[^1] >= Interlocked.Read(ref transferEndedAt),
            "The successful disconnect ran before the transfer ended.");
        Assert.Null(vm.CurrentSession);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task CloseSessionAsync_TransferThatNeverEnds_GivesUpAfterTheWait()
    {
        var backend = new FakeRemoteBackend([new RemoteProfile { Id = "test" }])
        {
            Session = new RemoteSession { SessionId = "session", ProfileId = "test", RootPath = "/" },
            Listing = new RemoteDirectoryListing { Path = "/" },
            // It ignores the cancel and never answers, and the session stays busy all along.
            CancellableTransferHandler = (_, _) => new TaskCompletionSource<RemoteTransferOutcome>().Task,
            DisconnectHandler = _ => throw new InvalidOperationException(BusyText),
        };
        var vm = Create(backend);
        await vm.LoadProfilesAsync();
        await vm.ConnectSelectedProfileAsync();
        _ = vm.UploadLocalFilesAsync([@"C:\a.bin"]);
        var wait = TimeSpan.FromMilliseconds(1000);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var closed = await vm.CloseSessionAsync(wait);

        clock.Stop();
        Assert.False(closed);
        Assert.InRange(clock.Elapsed, wait - TimeSpan.FromMilliseconds(50), wait + TimeSpan.FromSeconds(2));
        Assert.Null(backend.DisconnectedSessionId);
        Assert.NotNull(vm.CurrentSession);
        long[] attempts;
        lock (backend.DisconnectAttempts)
        {
            attempts = backend.DisconnectAttempts.ToArray();
        }

        Assert.InRange(attempts.Length, 1, (int)(wait.TotalMilliseconds / 200) + 1);
        for (var i = 1; i < attempts.Length; i++)
        {
            var gap = System.Diagnostics.Stopwatch.GetElapsedTime(attempts[i - 1], attempts[i]);
            Assert.True(gap >= TimeSpan.FromMilliseconds(190), $"Two disconnect attempts were {gap.TotalMilliseconds} ms apart.");
        }
    }

    [Fact]
    public async Task CloseSessionAsync_SessionStillBusyAfterTheTransfer_RetriesEvery200MsUntilItIsFree()
    {
        long transferEnded = 0;
        var refusals = 0;
        var backend = new FakeRemoteBackend([new RemoteProfile { Id = "test" }])
        {
            Session = new RemoteSession { SessionId = "session", ProfileId = "test", RootPath = "/" },
            Listing = new RemoteDirectoryListing { Path = "/" },
        };
        // The transfer answers at once on cancel, but the backend keeps the session locked for the next two disconnect
        // attempts. Counted, not timed: a 500 ms window let a loaded runner's first attempt land after it had closed
        // (CI run 42736768287).
        backend.CancellableTransferHandler = async (_, ct) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
            }

            Interlocked.Exchange(ref transferEnded, System.Diagnostics.Stopwatch.GetTimestamp());
            return new RemoteTransferOutcome { Cancelled = true };
        };
        backend.DisconnectHandler = _ =>
        {
            if (Interlocked.Read(ref transferEnded) == 0 || Interlocked.Increment(ref refusals) <= 2)
            {
                throw new InvalidOperationException(BusyText);
            }
        };
        var vm = Create(backend);
        await vm.LoadProfilesAsync();
        await vm.ConnectSelectedProfileAsync();
        var running = vm.UploadLocalFilesAsync([@"C:\a.bin"]);
        Assert.True(vm.CanCancelTransfer);

        var closed = await vm.CloseSessionAsync(TimeSpan.FromSeconds(5));

        Assert.True(closed);
        await running.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("session", backend.DisconnectedSessionId);
        Assert.Null(vm.CurrentSession);
        long firstAttempt;
        lock (backend.DisconnectAttempts)
        {
            firstAttempt = backend.DisconnectAttempts[0];
        }

        // Close waited for the cancelled transfer to release, then retried the busy session until it was free.
        var ended = Interlocked.Read(ref transferEnded);
        Assert.NotEqual(0, ended);
        Assert.True(firstAttempt >= ended, "The first disconnect ran before the cancelled transfer released the session.");
        AssertBusyRetries(backend, expectedAttempts: 3);
    }

    [Fact]
    public async Task CloseSessionAsync_SessionBusyWithoutATransfer_RetriesEvery200MsUntilItIsFree()
    {
        var backend = new FakeRemoteBackend([new RemoteProfile { Id = "test" }])
        {
            Session = new RemoteSession { SessionId = "session", ProfileId = "test", RootPath = "/" },
            Listing = new RemoteDirectoryListing { Path = "/" },
        };
        var vm = Create(backend);
        await vm.LoadProfilesAsync();
        await vm.ConnectSelectedProfileAsync();
        // No transfer runs, but another call (a listing, say) holds the session for the first two disconnect attempts.
        // Counted, not timed, so a loaded runner cannot skip the busy phase.
        var refusals = 0;
        backend.DisconnectHandler = _ =>
        {
            if (Interlocked.Increment(ref refusals) <= 2)
            {
                throw new InvalidOperationException(BusyText);
            }
        };

        var closed = await vm.CloseSessionAsync(TimeSpan.FromSeconds(5));

        Assert.True(closed);
        Assert.Equal("session", backend.DisconnectedSessionId);
        Assert.Null(vm.CurrentSession);
        AssertBusyRetries(backend, expectedAttempts: 3);
    }

    private static void AssertBusyRetries(FakeRemoteBackend backend, int expectedAttempts)
    {
        long[] attempts;
        lock (backend.DisconnectAttempts)
        {
            attempts = backend.DisconnectAttempts.ToArray();
        }

        // Refused as busy until the last attempt, which disconnected; each retry waited about 200 ms.
        Assert.True(
            attempts.Length == expectedAttempts,
            $"Expected {expectedAttempts} disconnect attempts (busy refusals, then success), saw {attempts.Length}.");
        for (var i = 1; i < attempts.Length; i++)
        {
            var gap = System.Diagnostics.Stopwatch.GetElapsedTime(attempts[i - 1], attempts[i]);
            Assert.True(gap >= TimeSpan.FromMilliseconds(190), $"Two disconnect attempts were {gap.TotalMilliseconds} ms apart.");
        }
    }

    [Fact]
    public async Task CloseSessionAsync_NoSession_ReturnsTrueAtOnce()
    {
        var backend = new FakeRemoteBackend([new RemoteProfile { Id = "test" }]);
        var vm = Create(backend);
        await vm.LoadProfilesAsync();

        var close = vm.CloseSessionAsync(TimeSpan.FromSeconds(5));

        Assert.True(close.IsCompleted);
        Assert.True(await close);
        Assert.Empty(backend.DisconnectAttempts);
    }

    [Fact]
    public async Task ConnectSelectedProfileAsync_ReturnsAMismatchOutcomeWithBothFingerprints()
    {
        var backend = new FakeRemoteBackend(
        [
            new RemoteProfile
            {
                Id = "prod",
                Name = "Production",
                Protocol = "sftp",
                Host = "example.com",
                Port = 22,
                Username = "deploy",
                RootPath = "/",
                AuthKind = "password",
                TrustedHostFingerprint = "SHA256:old",
            },
        ])
        {
            Session = new RemoteSession { SessionId = "s1", ProfileId = "prod", Protocol = "sftp", RootPath = "/" },
            Listing = new RemoteDirectoryListing { Path = "/", IsNetwork = true, Entries = [] },
        };
        backend.ConnectFailures.Enqueue(
            "SFTP host fingerprint mismatch. The server key changed since you trusted it. Expected SHA256:old, got SHA256:new");
        var viewModel = Create(backend);
        await viewModel.LoadProfilesAsync();

        var outcome = await viewModel.ConnectSelectedProfileAsync();

        Assert.False(outcome.Connected);
        Assert.True(outcome.IsHostKeyProblem);
        Assert.Null(outcome.UntrustedFingerprint);
        Assert.Equal("SHA256:old", outcome.MismatchExpected);
        Assert.Equal("SHA256:new", outcome.MismatchActual);
        Assert.Null(viewModel.CurrentSession);
        Assert.False(viewModel.IsConnected);
        Assert.False(viewModel.IsBusy);
        Assert.Equal("The server's key changed", viewModel.StatusText);
        Assert.Empty(backend.ListedPaths);
    }

    [Fact]
    public async Task RemoteCalls_AfterTheBackendRestarted_DropTheLostSessionAndAskToReconnect()
    {
        var backend = new FakeRemoteBackend(
        [
            new RemoteProfile { Id = "prod", Name = "Production", Protocol = "sftp", Host = "example.com", Port = 22 },
        ])
        {
            Session = new RemoteSession { SessionId = "s1", ProfileId = "prod", Protocol = "sftp", RootPath = "/" },
            Listing = new RemoteDirectoryListing
            {
                Path = "/",
                IsNetwork = true,
                Entries = [new RemoteFileEntry { Name = "app.txt", Path = "/app.txt" }],
            },
        };
        var viewModel = Create(backend);
        await viewModel.LoadProfilesAsync();
        await viewModel.ConnectSelectedProfileAsync();
        Assert.True(viewModel.IsConnected);

        // The backend restarted: its sessions are gone, and every call naming one fails with the exact text.
        backend.SessionsLost = true;
        await viewModel.RefreshRemoteDirectoryAsync();

        Assert.Null(viewModel.CurrentSession);
        Assert.False(viewModel.IsConnected);
        Assert.False(viewModel.IsBusy);
        Assert.Empty(viewModel.RemoteEntries);
        Assert.Equal("/", viewModel.RemotePath);
        Assert.Equal("The connection was lost. Connect again.", viewModel.StatusText);
        Assert.True(viewModel.CanConnect);

        // The same holds for a mutation and for a transfer, which keeps its selections for a retry.
        backend.SessionsLost = false;
        await viewModel.ConnectSelectedProfileAsync();
        backend.SessionsLost = true;
        await viewModel.CreateRemoteDirectoryAsync("incoming");
        Assert.Null(viewModel.CurrentSession);
        Assert.Equal("The connection was lost. Connect again.", viewModel.StatusText);

        backend.SessionsLost = false;
        await viewModel.ConnectSelectedProfileAsync();
        backend.SessionsLost = true;
        await viewModel.UploadLocalFilesAsync([@"C:\Uploads\report.pdf"]);
        Assert.Null(viewModel.CurrentSession);
        Assert.False(viewModel.IsBusy);
        Assert.False(viewModel.CanCancelTransfer);
        Assert.Equal("The connection was lost. Connect again.", viewModel.StatusText);

        backend.SessionsLost = false;
        await viewModel.ConnectSelectedProfileAsync();
        Assert.True(viewModel.CanRetryTransfers);
    }
}
