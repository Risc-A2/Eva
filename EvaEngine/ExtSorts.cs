using System;

namespace EvaEngine;

public static class ExtSorts
{
    public static void Swap<T>(ref T a, ref T b)
    {
        T temp = a;
        a = b;
        b = temp;
    }
    public static void Swap<T>(Span<T> span, int i, int j)
    {
        /*
        // Traditional swap
        T temp = span[i];
        span[i] = span[j];
        span[j] = temp;
        */
        // C# 7 tuple swap
        // More efficient than traditional swap
        
        (span[i], span[j]) = (span[j], span[i]);
    }

    /// <summary>
    /// Performs an in-place quicksort on a span of elements using the left-right partitioning scheme.
    /// </summary>
    /// <typeparam name="T">The type of elements in the span.</typeparam>
    /// <param name="span">The span of elements to be sorted.</param>
    /// <param name="left">The starting index of the range to sort.</param>
    /// <param name="right">The ending index of the range to sort.</param>
    /// <param name="comparison">
    /// A delegate that compares two elements and returns:
    /// <list type="bullet">
    /// <item><description>A value less than zero if the first element is less than the second.</description></item>
    /// <item><description>Zero if the first element is equal to the second.</description></item>
    /// <item><description>A value greater than zero if the first element is greater than the second.</description></item>
    /// </list>
    /// </param>
    /// <remarks>
    /// This method uses the quicksort algorithm with a pivot chosen as the middle element of the range.
    /// The method modifies the original span in place.
    /// </remarks>
    public static void QuickSortLR<T>(this Span<T> span, int left, int right, Comparison<T> comparison)
    {
        if (left < right)
        {
            int i = left;
            int j = right;
            T pivot = span[(left + right) / 2];

            while (i <= j)
            {
                while (comparison(span[i], pivot) < 0) i++;
                while (comparison(span[j], pivot) > 0) j--;

                if (i <= j)
                {
                    Swap(span, i, j);
                    i++;
                    j--;
                }
            }

            if (left < j)
                QuickSortLR(span, left, j, comparison);
            if (i < right)
                QuickSortLR(span, i, right, comparison);
        }
    }
}
