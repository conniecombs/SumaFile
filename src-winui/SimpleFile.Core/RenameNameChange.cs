using System.Globalization;

namespace SimpleFile.Core;

public sealed record RenameNameChange(int OldStart, int OldLength, int NewStart, int NewLength)
{
    public static IReadOnlyList<RenameNameChange> Between(string before, string after)
    {
        var left = StringInfo.ParseCombiningCharacters(before);
        var right = StringInfo.ParseCombiningCharacters(after);
        // Oversized, invalid proposals must not cause quadratic work on the UI thread.
        if ((long)left.Length * right.Length > 262_144) return [];
        int OldOffset(int index) => index < left.Length ? left[index] : before.Length;
        int NewOffset(int index) => index < right.Length ? right[index] : after.Length;
        bool Equal(int i, int j) => before.AsSpan(OldOffset(i), OldOffset(i + 1) - OldOffset(i))
            .SequenceEqual(after.AsSpan(NewOffset(j), NewOffset(j + 1) - NewOffset(j)));
        var common = new int[left.Length + 1, right.Length + 1];
        for (var i = left.Length - 1; i >= 0; i--)
            for (var j = right.Length - 1; j >= 0; j--)
                common[i, j] = Equal(i, j) ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);

        var changes = new List<RenameNameChange>();
        var oldIndex = 0;
        var newIndex = 0;
        int? oldStart = null;
        var newStart = 0;
        void Flush()
        {
            if (oldStart is not { } start) return;
            changes.Add(new(OldOffset(start), OldOffset(oldIndex) - OldOffset(start),
                NewOffset(newStart), NewOffset(newIndex) - NewOffset(newStart)));
            oldStart = null;
        }
        while (oldIndex < left.Length || newIndex < right.Length)
        {
            if (oldIndex < left.Length && newIndex < right.Length && Equal(oldIndex, newIndex))
            {
                Flush();
                oldIndex++;
                newIndex++;
                continue;
            }
            if (oldStart is null) { oldStart = oldIndex; newStart = newIndex; }
            if (newIndex == right.Length || (oldIndex < left.Length && common[oldIndex + 1, newIndex] >= common[oldIndex, newIndex + 1])) oldIndex++;
            else newIndex++;
        }
        Flush();
        return changes;
    }
}
