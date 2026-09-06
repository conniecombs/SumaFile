using System.IO.MemoryMappedFiles;
using System.Text;
using SimpleFile.Ipc;
using Xunit;

namespace SimpleFile.Tests;

public sealed class SharedMemoryTests
{
    [Fact]
    public void Read_ValidSection_ReturnsExactBytes()
    {
        var sectionName = $"Local\\SumaFile_Test_SHM_{Guid.NewGuid():N}";
        var expected = Encoding.UTF8.GetBytes("Shared memory round-trip test payload from WinUI tests!");

        using (var mmf = MemoryMappedFile.CreateNew(sectionName, expected.Length))
        using (var stream = mmf.CreateViewStream(0, expected.Length))
        {
            stream.Write(expected, 0, expected.Length);
            stream.Flush();

            var actual = SharedMemoryReader.Read(sectionName, expected.Length);

            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void TryRead_NonExistentSection_ReturnsFalse()
    {
        var nonExistentName = $"Local\\SumaFile_NonExistent_{Guid.NewGuid():N}";

        var success = SharedMemoryReader.TryRead(nonExistentName, 1024, out var data);

        Assert.False(success);
        Assert.Null(data);
    }
}
