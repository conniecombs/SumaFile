namespace SimpleFile.Core;

public static class ListReplace
{
    public static bool Apply<T>(IList<T> target, IReadOnlyList<T> source, Func<T, T, bool> same)
    {
        if (target.Count == source.Count)
        {
            var changed = false;
            for (var index = 0; index < source.Count; index++)
            {
                if (!same(target[index], source[index]))
                {
                    target[index] = source[index];
                    changed = true;
                }
            }

            return changed;
        }

        if (target.Count < source.Count)
        {
            var prefixMatches = true;
            for (var index = 0; index < target.Count; index++)
            {
                if (!same(target[index], source[index]))
                {
                    prefixMatches = false;
                    break;
                }
            }

            if (prefixMatches)
            {
                for (var index = target.Count; index < source.Count; index++)
                {
                    target.Add(source[index]);
                }

                return true;
            }
        }

        target.Clear();
        foreach (var item in source)
        {
            target.Add(item);
        }

        return true;
    }
}
