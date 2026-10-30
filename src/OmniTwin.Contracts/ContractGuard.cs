using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace OmniTwin.Contracts;

internal static class ContractGuard
{
    internal static float Finite(float value, string name)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            throw new ArgumentOutOfRangeException(name, "Value must be finite.");
        return value;
    }

    internal static float Range(float value, float minimum, float maximum, string name)
    {
        Finite(value, name);
        if (value < minimum || value > maximum)
            throw new ArgumentOutOfRangeException(name, $"Value must be in [{minimum}, {maximum}].");
        return value;
    }

    internal static float Positive(float value, string name)
    {
        Finite(value, name);
        if (value <= 0)
            throw new ArgumentOutOfRangeException(name, "Value must be positive.");
        return value;
    }

    internal static int Id(int value, string name)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(name, "ID must be nonnegative.");
        return value;
    }

    internal static long Nonnegative(long value, string name)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(name, "Value must be nonnegative.");
        return value;
    }

    internal static string Text(string value, int maximumLength, string name)
    {
        if (value is null) throw new ArgumentNullException(name);
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            throw new ArgumentException($"Text must be nonempty and at most {maximumLength} characters.", name);
        return value;
    }

    internal static ReadOnlyCollection<T> CopyUnique<T>(IReadOnlyList<T> source, Func<T, int> getId, string name)
        where T : class
    {
        if (source is null) throw new ArgumentNullException(name);
        var copy = new T[source.Count];
        var ids = new HashSet<int>();
        for (var i = 0; i < copy.Length; i++)
        {
            var item = source[i];
            if (item is null) throw new ArgumentException("Collection cannot contain null elements.", name);
            if (!ids.Add(getId(item))) throw new ArgumentException("Collection contains duplicate IDs.", name);
            copy[i] = item;
        }
        return Array.AsReadOnly(copy);
    }
}
