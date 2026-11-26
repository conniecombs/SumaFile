namespace SimpleFile.Core;

/// <summary>Removes a folder only when nothing is in it.</summary>
public static class EmptyFolder
{
    /// <summary>
    /// Removes the folder at <paramref name="path"/> when it holds nothing. Returns true when nothing is left
    /// at the path, which includes a folder that was not there. Returns false when the folder stays: it holds
    /// something, or the system refused to remove it. The file system does the check and the removal as one
    /// step, so an item put into the folder at any moment keeps it. A link to a folder is removed as a link,
    /// and a file is never removed.
    /// </summary>
    public static bool Remove(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return !File.Exists(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return OperatingSystem.IsWindows() && RemoveReadOnly(path);
        }
    }

    // Windows refuses to remove a folder marked read-only, and it marks every folder that has a custom icon.
    private static bool RemoveReadOnly(string path)
    {
        try
        {
            var folder = new DirectoryInfo(path);
            if (!folder.Exists || (folder.Attributes & FileAttributes.ReadOnly) == 0)
            {
                return false;
            }

            folder.Attributes &= ~FileAttributes.ReadOnly;
            try
            {
                // Not recursive: a folder that holds something is still refused.
                folder.Delete();
                return true;
            }
            catch
            {
                folder.Attributes |= FileAttributes.ReadOnly;
                throw;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
