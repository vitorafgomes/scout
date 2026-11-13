namespace Scout.Skills;

/// <summary>
/// Vector helpers for in-memory similarity matching over a small skill set.
/// </summary>
internal static class VectorMath
{
    /// <summary>
    /// Computes the cosine similarity between two equal-length vectors. Returns
    /// 0 when lengths differ or either vector has zero magnitude.
    /// </summary>
    /// <param name="a">The first vector.</param>
    /// <param name="b">The second vector.</param>
    /// <returns>Cosine similarity in the range [-1, 1], or 0 when undefined.</returns>
    public static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length || a.Length == 0)
        {
            return 0d;
        }

        double dot = 0d;
        double magA = 0d;
        double magB = 0d;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA <= 0d || magB <= 0d)
        {
            return 0d;
        }

        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}
