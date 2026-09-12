namespace SolScan.Processing.Math;

/// <summary>
/// Solves a general <c>NxN</c> linear system <c>A*x = b</c> via Gaussian elimination with partial
/// pivoting - used by <c>SolScan.Processing.Shg.BackgroundNeutralizer</c> to solve the 6-term normal
/// equations of its 2nd-order background-model fit (astro4j's own equivalent leans on Apache Commons
/// Math's <c>OLSMultipleLinearRegression</c>/<c>LUDecomposition</c>; SolScan has no such dependency, and
/// a plain pivoted elimination is more than adequate for the small, well-conditioned systems this app
/// ever builds - see <see cref="LinearRegression"/>'s own doc comment for the same "no generic matrix
/// library" stance on the simpler 1D quadratic case).
/// </summary>
public static class LinearSystem
{
    /// <summary><paramref name="a"/> is mutated in place as scratch space (its own copy, not the
    /// caller's, if that matters); returns null if the system is singular (or effectively so) rather
    /// than dividing by a near-zero pivot.</summary>
    public static double[]? Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        var matrix = (double[,])a.Clone();
        var rhs = (double[])b.Clone();

        for (var pivot = 0; pivot < n; pivot++)
        {
            var maxRow = pivot;
            var maxValue = System.Math.Abs(matrix[pivot, pivot]);
            for (var row = pivot + 1; row < n; row++)
            {
                var value = System.Math.Abs(matrix[row, pivot]);
                if (value > maxValue)
                {
                    maxRow = row;
                    maxValue = value;
                }
            }

            if (maxValue < 1e-12)
            {
                return null;
            }

            if (maxRow != pivot)
            {
                for (var col = 0; col < n; col++)
                {
                    (matrix[pivot, col], matrix[maxRow, col]) = (matrix[maxRow, col], matrix[pivot, col]);
                }

                (rhs[pivot], rhs[maxRow]) = (rhs[maxRow], rhs[pivot]);
            }

            for (var row = pivot + 1; row < n; row++)
            {
                var factor = matrix[row, pivot] / matrix[pivot, pivot];
                for (var col = pivot; col < n; col++)
                {
                    matrix[row, col] -= factor * matrix[pivot, col];
                }

                rhs[row] -= factor * rhs[pivot];
            }
        }

        var x = new double[n];
        for (var row = n - 1; row >= 0; row--)
        {
            var sum = rhs[row];
            for (var col = row + 1; col < n; col++)
            {
                sum -= matrix[row, col] * x[col];
            }

            x[row] = sum / matrix[row, row];
        }

        return x;
    }
}
