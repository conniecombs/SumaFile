#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SimpleFile.Ipc;

public sealed partial class NamedPipeJsonClient
{
    public async Task<IReadOnlyList<DriveInfo>> ListDriveAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        var drives = await InvokeAsync<DriveInfo[]>(
                Protocol.ListDrivesMethod,
                new { mode = "drive", path },
                cancellationToken)
            .ConfigureAwait(false);
        return drives;
    }
}
