namespace SimpleFile.Core;

public static class ListReplace
{
    /// <summary>
    /// The most rows a single edit may remove and insert before the list is rebuilt instead. A file added,
    /// removed or renamed in a large folder is one or two rows; a different folder is all of them.
    /// </summary>
    public const int MaxEditRows = 64;

    /// <summary>
    /// Makes <paramref name="target"/> equal to <paramref name="source"/> with the fewest changes a bound list
    /// has to hear of, and returns whether anything changed. Rows the two lists share at the start and at the end
    /// are left alone, so a row added or removed in the middle is one insert or one removal: the list keeps its
    /// scroll position and the selection of every other row. Lists of the same length are updated in place, a
    /// longer list that starts with the target is appended to, and anything else is cleared and filled again.
    /// </summary>
    public static bool Apply<T>(IList<T> target, IReadOnlyList<T> source, Func<T, T, bool> same)
    {
        var shared = Math.Min(target.Count, source.Count);
        var prefix = 0;
        while (prefix < shared && same(target[prefix], source[prefix]))
        {
            prefix++;
        }

        if (prefix == target.Count && prefix == source.Count)
        {
            return false;
        }

        var suffix = 0;
        while (suffix < shared - prefix
            && same(target[target.Count - 1 - suffix], source[source.Count - 1 - suffix]))
        {
            suffix++;
        }

        var removed = target.Count - prefix - suffix;
        var inserted = source.Count - prefix - suffix;
        var sameLength = removed == inserted;
        var append = removed == 0 && suffix == 0;
        var smallEdit = prefix + suffix > 0 && removed + inserted <= MaxEditRows;
        if (!sameLength && !append && !smallEdit)
        {
            target.Clear();
            foreach (var item in source)
            {
                target.Add(item);
            }

            return true;
        }

        // Rows that changed in place first, then the rows the middle lost or gained.
        var replaced = Math.Min(removed, inserted);
        for (var index = prefix; index < prefix + replaced; index++)
        {
            if (!same(target[index], source[index]))
            {
                target[index] = source[index];
            }
        }

        for (var index = prefix + removed - 1; index >= prefix + replaced; index--)
        {
            target.RemoveAt(index);
        }

        for (var index = prefix + replaced; index < prefix + inserted; index++)
        {
            if (index == target.Count)
            {
                target.Add(source[index]);
            }
            else
            {
                target.Insert(index, source[index]);
            }
        }

        return true;
    }
}
