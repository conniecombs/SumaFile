using SimpleFile.Core;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public class RemoteManagerViewModelTests
{
    [Fact]
    public async Task LoadProfilesAsync_SelectsFirstProfileAndEnablesActions()
    {
        var ipc = new RemoteProfileIpc(
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
        var viewModel = new RemoteManagerViewModel(new FileOperationService(ipc));

        Assert.False(viewModel.CanConnect);

        await viewModel.LoadProfilesAsync();

        var profile = Assert.Single(viewModel.Profiles);
        Assert.Same(profile, viewModel.SelectedProfile);
        Assert.Equal("Production", profile.Name);
        Assert.Equal("Production - sftp://deploy@example.com:22/var/www", viewModel.SelectedProfileSummary);
        Assert.Equal("1 remote profile", viewModel.StatusText);
        Assert.True(viewModel.CanConnect);
        Assert.True(viewModel.CanDeleteProfile);
        Assert.Equal(1, ipc.ListCalls);
    }

    [Fact]
    public async Task LoadProfilesAsync_WhenEmptyShowsEmptyStateAndDisablesActions()
    {
        var viewModel = new RemoteManagerViewModel(new FileOperationService(new RemoteProfileIpc([])));

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
        var viewModel = new RemoteManagerViewModel(new FileOperationService(new RemoteProfileIpc(
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
        ])));

        viewModel.RequestProfileSelection("prod");
        await viewModel.LoadProfilesAsync();

        Assert.Equal("prod", viewModel.SelectedProfile?.Id);
        Assert.Equal("Production", viewModel.SelectedProfile?.Name);
        Assert.Equal("Production - sftp://deploy@example.com:22/var/www", viewModel.SelectedProfileSummary);
    }

    [Fact]
    public async Task ConnectSelectedProfileAsync_ConnectsAndLoadsRemoteListing()
    {
        var ipc = new RemoteProfileIpc(
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
            Listing = new DirectoryListing
            {
                Path = "/",
                Parent = null,
                IsNetwork = true,
                Entries =
                [
                    new FileEntry
                    {
                        Name = "release",
                        Path = "/release",
                        IsDir = true,
                        Modified = "2026-09-21T00:00:00Z",
                    },
                ],
            },
        };
        var viewModel = new RemoteManagerViewModel(new FileOperationService(ipc));
        await viewModel.LoadProfilesAsync();

        await viewModel.ConnectSelectedProfileAsync("not-returned");

        Assert.Equal("prod", ipc.ConnectedProfileId);
        Assert.Equal("not-returned", ipc.ConnectedSecret);
        Assert.True(viewModel.IsConnected);
        Assert.False(viewModel.CanConnect);
        Assert.True(viewModel.CanDisconnect);
        Assert.Equal("Connected to Production", viewModel.StatusText);
        Assert.Equal("/", viewModel.RemotePath);
        Assert.Equal("release", Assert.Single(viewModel.RemoteEntries).Name);
    }

    [Fact]
    public async Task RemoteMutationMethods_UpdateDirectoryEntriesAndStatus()
    {
        var ipc = new RemoteProfileIpc(
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
            Listing = new DirectoryListing
            {
                Path = "/",
                Parent = null,
                IsNetwork = true,
                Entries = [],
            },
        };
        var viewModel = new RemoteManagerViewModel(new FileOperationService(ipc));
        await viewModel.LoadProfilesAsync();
        await viewModel.ConnectSelectedProfileAsync();

        await viewModel.CreateRemoteDirectoryAsync("incoming");

        Assert.Equal(("remote-session-1", "/", "incoming"), ipc.CreatedDirectory);
        var created = Assert.Single(viewModel.RemoteEntries);
        Assert.Equal("incoming", created.Name);
        Assert.Equal("Created incoming", viewModel.StatusText);

        await viewModel.RenameRemoteEntryAsync(created, "archive");

        Assert.Equal(("remote-session-1", "/incoming", "archive"), ipc.RenamedEntry);
        var renamed = Assert.Single(viewModel.RemoteEntries);
        Assert.Equal("archive", renamed.Name);
        Assert.Equal("/archive", renamed.Path);
        Assert.Equal("Renamed incoming to archive", viewModel.StatusText);

        await viewModel.DeleteRemoteEntriesAsync([renamed]);

        var deletedEntries = Assert.NotNull(ipc.DeletedEntries);
        Assert.Equal("remote-session-1", deletedEntries.RemoteSessionId);
        Assert.Equal(["/archive"], deletedEntries.Paths);
        Assert.Empty(viewModel.RemoteEntries);
        Assert.Equal("Deleted 1 remote item", viewModel.StatusText);
    }

    [Fact]
    public async Task RemoteTransferMethods_DownloadAndUploadFiles()
    {
        var ipc = new RemoteProfileIpc(
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
            Listing = new DirectoryListing
            {
                Path = "/incoming",
                Parent = null,
                IsNetwork = true,
                Entries =
                [
                    new FileEntry
                    {
                        Name = "app.txt",
                        Path = "/incoming/app.txt",
                        Modified = "2026-09-21T00:00:00Z",
                    },
                ],
            },
        };
        var viewModel = new RemoteManagerViewModel(new FileOperationService(ipc));
        await viewModel.LoadProfilesAsync();
        await viewModel.ConnectSelectedProfileAsync();
        var remoteEntry = Assert.Single(viewModel.RemoteEntries);

        await viewModel.DownloadRemoteEntriesAsync([remoteEntry], @"C:\Downloads");

        Assert.Equal(("remote-session-1", "/incoming/app.txt", @"C:\Downloads\app.txt"), ipc.DownloadedFile);
        Assert.Equal("Downloaded 1 remote file", viewModel.StatusText);
        var downloadRecord = Assert.Single(viewModel.Transfers);
        Assert.Equal("Download", downloadRecord.Direction);
        Assert.Equal("Completed", downloadRecord.Status);
        Assert.Equal("/incoming/app.txt", downloadRecord.RemotePath);
        Assert.Equal(@"C:\Downloads\app.txt", downloadRecord.LocalPath);

        await viewModel.UploadLocalFilesAsync([@"C:\Uploads\report.pdf"]);

        Assert.Equal(("remote-session-1", @"C:\Uploads\report.pdf", "/incoming/report.pdf"), ipc.UploadedFile);
        Assert.Contains(viewModel.RemoteEntries, entry => entry.Name == "report.pdf");
        Assert.Equal("Uploaded 1 file", viewModel.StatusText);
        var uploadRecord = Assert.Single(viewModel.Transfers, transfer => transfer.Direction == "Upload");
        Assert.Equal("Completed", uploadRecord.Status);
        Assert.Equal("/incoming/report.pdf", uploadRecord.RemotePath);
        Assert.Equal(@"C:\Uploads\report.pdf", uploadRecord.LocalPath);
    }

    [Fact]
    public async Task RemoteNavigationMethods_BrowseFoldersAndParents()
    {
        var ipc = new RemoteProfileIpc(
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
                ["/"] = new DirectoryListing
                {
                    Path = "/",
                    Parent = null,
                    IsNetwork = true,
                    Entries =
                    [
                        new FileEntry
                        {
                            Name = "incoming",
                            Path = "/incoming",
                            IsDir = true,
                            Modified = "2026-09-21T00:00:00Z",
                        },
                    ],
                },
                ["/incoming"] = new DirectoryListing
                {
                    Path = "/incoming",
                    Parent = "/",
                    IsNetwork = true,
                    Entries =
                    [
                        new FileEntry
                        {
                            Name = "app.txt",
                            Path = "/incoming/app.txt",
                            Modified = "2026-09-21T00:00:00Z",
                        },
                    ],
                },
            },
        };
        var viewModel = new RemoteManagerViewModel(new FileOperationService(ipc));
        await viewModel.LoadProfilesAsync();
        await viewModel.ConnectSelectedProfileAsync();

        var folder = Assert.Single(viewModel.RemoteEntries);
        await viewModel.NavigateRemoteEntryAsync(folder);

        Assert.Equal("/incoming", viewModel.RemotePath);
        Assert.Equal("app.txt", Assert.Single(viewModel.RemoteEntries).Name);

        await viewModel.GoToParentRemoteDirectoryAsync();

        Assert.Equal("/", viewModel.RemotePath);
        Assert.Equal(["/", "/incoming", "/"], ipc.ListedPaths);
    }

    [Fact]
    public async Task LocalNavigationMethods_LoadBrowseAndReturnToParent()
    {
        var ipc = new RemoteProfileIpc([])
        {
            LocalListingsByPath =
            {
                [@"C:\Work"] = new DirectoryListing
                {
                    Path = @"C:\Work",
                    Parent = @"C:\",
                    Entries =
                    [
                        new FileEntry
                        {
                            Name = "site",
                            Path = @"C:\Work\site",
                            IsDir = true,
                            Modified = "2026-09-21T00:00:00Z",
                        },
                    ],
                },
                [@"C:\Work\site"] = new DirectoryListing
                {
                    Path = @"C:\Work\site",
                    Parent = @"C:\Work",
                    Entries =
                    [
                        new FileEntry
                        {
                            Name = "index.html",
                            Path = @"C:\Work\site\index.html",
                            Modified = "2026-09-21T00:00:00Z",
                        },
                    ],
                },
                [@"C:\"] = new DirectoryListing
                {
                    Path = @"C:\",
                    Parent = null,
                    Entries = [],
                },
            },
        };
        var viewModel = new RemoteManagerViewModel(new FileOperationService(ipc));

        await viewModel.NavigateLocalPathAsync(@"C:\Work");

        Assert.Equal(@"C:\Work", viewModel.LocalPath);
        Assert.Equal("site", Assert.Single(viewModel.LocalEntries).Name);
        Assert.Equal("Local path: C:\\Work", viewModel.StatusText);

        await viewModel.NavigateLocalEntryAsync(Assert.Single(viewModel.LocalEntries));

        Assert.Equal(@"C:\Work\site", viewModel.LocalPath);
        Assert.Equal("index.html", Assert.Single(viewModel.LocalEntries).Name);

        await viewModel.GoToParentLocalDirectoryAsync();

        Assert.Equal(@"C:\Work", viewModel.LocalPath);
        Assert.Equal([@"C:\Work", @"C:\Work\site", @"C:\Work"], ipc.LocalListedPaths);
    }

    [Fact]
    public async Task WorkspaceTransferMethods_UseCurrentLocalAndRemoteFolders()
    {
        var ipc = new RemoteProfileIpc(
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
            Listing = new DirectoryListing
            {
                Path = "/incoming",
                Parent = "/",
                IsNetwork = true,
                Entries =
                [
                    new FileEntry
                    {
                        Name = "server.log",
                        Path = "/incoming/server.log",
                        Modified = "2026-09-21T00:00:00Z",
                    },
                ],
            },
            LocalListingsByPath =
            {
                [@"C:\Deploy"] = new DirectoryListing
                {
                    Path = @"C:\Deploy",
                    Parent = @"C:\",
                    Entries =
                    [
                        new FileEntry
                        {
                            Name = "index.html",
                            Path = @"C:\Deploy\index.html",
                            Modified = "2026-09-21T00:00:00Z",
                        },
                    ],
                },
            },
        };
        var viewModel = new RemoteManagerViewModel(new FileOperationService(ipc));
        await viewModel.LoadProfilesAsync();
        await viewModel.ConnectSelectedProfileAsync();
        await viewModel.NavigateLocalPathAsync(@"C:\Deploy");

        await viewModel.DownloadRemoteEntriesToLocalAsync(viewModel.RemoteEntries);

        Assert.Equal(("remote-session-1", "/incoming/server.log", @"C:\Deploy\server.log"), ipc.DownloadedFile);
        Assert.Equal("Downloaded 1 remote file to C:\\Deploy", viewModel.StatusText);

        await viewModel.UploadLocalEntriesToRemoteAsync(viewModel.LocalEntries);

        Assert.Equal(("remote-session-1", @"C:\Deploy\index.html", "/incoming/index.html"), ipc.UploadedFile);
        Assert.Contains(viewModel.RemoteEntries, entry => entry.Name == "index.html");
        Assert.Equal("Uploaded 1 local file to /incoming", viewModel.StatusText);
    }


    [Fact]
    public async Task SaveProfileAsync_AddsSavedProfileAndSelectsIt()
    {
        var ipc = new RemoteProfileIpc([]);
        var viewModel = new RemoteManagerViewModel(new FileOperationService(ipc));
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
        Assert.Equal("saved-secret", ipc.SavedSecret);
        Assert.Equal("Saved Production", viewModel.StatusText);
        Assert.True(viewModel.CanConnect);
    }

    [Fact]
    public async Task DeleteSelectedProfileAsync_RemovesSelectedProfile()
    {
        var ipc = new RemoteProfileIpc(
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
        var viewModel = new RemoteManagerViewModel(new FileOperationService(ipc));
        await viewModel.LoadProfilesAsync();

        await viewModel.DeleteSelectedProfileAsync();

        Assert.Equal("prod", ipc.DeletedProfileId);
        Assert.Empty(viewModel.Profiles);
        Assert.Null(viewModel.SelectedProfile);
        Assert.Equal("Deleted Production", viewModel.StatusText);
        Assert.False(viewModel.CanConnect);
    }

    [Fact]
    public async Task TestProfileAsync_ReturnsResultAndUpdatesStatus()
    {
        var ipc = new RemoteProfileIpc([])
        {
            TestResult = new RemoteConnectionTestResult
            {
                Ok = true,
                Message = "Profile settings are valid.",
                Capabilities = ["sftp"],
            },
        };
        var viewModel = new RemoteManagerViewModel(new FileOperationService(ipc));

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
        Assert.Equal("saved-secret", ipc.TestSecret);
        Assert.Equal("Profile settings are valid.", viewModel.StatusText);
    }

    private sealed class RemoteProfileIpc(RemoteProfile[] profiles) : NullIpc
    {
        public int ListCalls { get; private set; }
        public string? ConnectedProfileId { get; private set; }
        public string? ConnectedSecret { get; private set; }
        public RemoteProfileInput? SavedProfile { get; private set; }
        public string? SavedSecret { get; private set; }
        public string? DeletedProfileId { get; private set; }
        public RemoteProfileInput? TestedProfile { get; private set; }
        public string? TestSecret { get; private set; }
        public (string RemoteSessionId, string Path, string Name)? CreatedDirectory { get; private set; }
        public (string RemoteSessionId, string Path, string NewName)? RenamedEntry { get; private set; }
        public (string RemoteSessionId, string[] Paths)? DeletedEntries { get; private set; }
        public (string RemoteSessionId, string RemotePath, string LocalPath)? DownloadedFile { get; private set; }
        public (string RemoteSessionId, string LocalPath, string RemotePath)? UploadedFile { get; private set; }
        public List<string> ListedPaths { get; } = [];
        public List<string> LocalListedPaths { get; } = [];
        public Dictionary<string, DirectoryListing> ListingsByPath { get; init; } = [];
        public Dictionary<string, DirectoryListing> LocalListingsByPath { get; init; } = [];
        public RemoteSession Session { get; init; } = new();
        public DirectoryListing Listing { get; init; } = new();
        public RemoteConnectionTestResult TestResult { get; init; } = new()
        {
            Ok = true,
            Message = "Profile settings are valid.",
            Capabilities = [],
        };

        public override Task<RemoteProfile[]> RemoteListProfilesAsync(CancellationToken ct = default)
        {
            ListCalls++;
            return Task.FromResult(profiles);
        }

        public override Task<RemoteSession> RemoteConnectAsync(
            string profileId,
            string? secret = null,
            CancellationToken ct = default)
        {
            ConnectedProfileId = profileId;
            ConnectedSecret = secret;
            return Task.FromResult(Session);
        }

        public override Task<RemoteProfile> RemoteSaveProfileAsync(
            RemoteProfileInput profile,
            string? secret = null,
            CancellationToken ct = default)
        {
            SavedProfile = profile;
            SavedSecret = secret;
            return Task.FromResult(new RemoteProfile
            {
                Id = string.IsNullOrWhiteSpace(profile.Id) ? "prod" : profile.Id,
                Name = profile.Name,
                Protocol = profile.Protocol,
                Host = profile.Host,
                Port = profile.Port,
                Username = profile.Username,
                RootPath = profile.RootPath,
                AuthKind = profile.AuthKind,
                InsecurePlainFtp = profile.InsecurePlainFtp,
                PassiveMode = profile.PassiveMode,
                CredentialTarget = profile.CredentialTarget,
                PrivateKeyPath = profile.PrivateKeyPath,
                TrustedHostFingerprint = profile.TrustedHostFingerprint,
            });
        }

        public override Task RemoteDeleteProfileAsync(string profileId, CancellationToken ct = default)
        {
            DeletedProfileId = profileId;
            return Task.CompletedTask;
        }

        public override Task<RemoteConnectionTestResult> RemoteTestProfileAsync(
            RemoteProfileInput profile,
            string? secret = null,
            CancellationToken ct = default)
        {
            TestedProfile = profile;
            TestSecret = secret;
            return Task.FromResult(TestResult);
        }

        public override Task<DirectoryListing> RemoteListDirectoryAsync(
            string remoteSessionId,
            string path,
            CancellationToken ct = default)
        {
            Assert.Equal(Session.SessionId, remoteSessionId);
            ListedPaths.Add(path);
            if (ListingsByPath.TryGetValue(path, out var listing))
            {
                return Task.FromResult(listing);
            }

            Assert.Equal(string.IsNullOrWhiteSpace(Listing.Path) ? "/" : Listing.Path, path);
            return Task.FromResult(Listing);
        }

        public override Task<DirectoryListing> ListDirectoryAsync(
            string path,
            Action<DirectoryListingChunk>? onChunk = null,
            CancellationToken cancellationToken = default,
            ListDirectoryOptions? options = null)
        {
            LocalListedPaths.Add(path);
            if (LocalListingsByPath.TryGetValue(path, out var listing))
            {
                return Task.FromResult(listing);
            }

            throw new InvalidOperationException($"No local listing configured for {path}.");
        }

        public override Task<FileEntry> RemoteCreateDirectoryAsync(
            string remoteSessionId,
            string path,
            string name,
            CancellationToken ct = default)
        {
            CreatedDirectory = (remoteSessionId, path, name);
            return Task.FromResult(new FileEntry
            {
                Name = name,
                Path = "/" + name,
                IsDir = true,
                Modified = "2026-09-21T00:00:00Z",
            });
        }

        public override Task<FileEntry> RemoteRenameEntryAsync(
            string remoteSessionId,
            string path,
            string newName,
            CancellationToken ct = default)
        {
            RenamedEntry = (remoteSessionId, path, newName);
            return Task.FromResult(new FileEntry
            {
                Name = newName,
                Path = "/" + newName,
                IsDir = true,
                Modified = "2026-09-21T00:00:00Z",
            });
        }

        public override Task<string[]> RemoteDeleteEntriesAsync(
            string remoteSessionId,
            string[] paths,
            CancellationToken ct = default)
        {
            DeletedEntries = (remoteSessionId, paths);
            return Task.FromResult(paths);
        }

        public override Task<string> RemoteDownloadFileAsync(
            string remoteSessionId,
            string remotePath,
            string localPath,
            CancellationToken ct = default)
        {
            DownloadedFile = (remoteSessionId, remotePath, localPath);
            return Task.FromResult(localPath);
        }

        public override Task<FileEntry> RemoteUploadFileAsync(
            string remoteSessionId,
            string localPath,
            string remotePath,
            CancellationToken ct = default)
        {
            UploadedFile = (remoteSessionId, localPath, remotePath);
            return Task.FromResult(new FileEntry
            {
                Name = Path.GetFileName(localPath),
                Path = remotePath,
                Modified = "2026-09-21T00:00:00Z",
                Size = 11,
            });
        }
    }
}
