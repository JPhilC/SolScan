namespace SolScan.Processing.Math;

/// <summary>
/// A general (not necessarily symmetric) real 3x3 matrix - the largest matrix
/// <see cref="EllipseRegression"/>'s Halir-Flusser fit and the geometry-correction ellipse-transform
/// math ever need, so this stays hand-rolled rather than porting astro4j's own generic <c>DoubleMatrix</c>
/// (which leans on Apache Commons Math's <c>EigenDecomposition</c> for the ellipse fit's eigensystem
/// solve). SolScan has no such dependency, so <see cref="SolveRealEigenpairs"/> instead solves the 3x3
/// characteristic cubic in closed form (Cardano/trigonometric method) and recovers each eigenvector as
/// the best-conditioned cross product of two rows of the singular <c>(M - lambda*I)</c> - exact for a
/// fixed 3x3, not iterative.
/// </summary>
public readonly struct Matrix3x3(
    double m00, double m01, double m02,
    double m10, double m11, double m12,
    double m20, double m21, double m22)
{
    public double M00 { get; } = m00;
    public double M01 { get; } = m01;
    public double M02 { get; } = m02;
    public double M10 { get; } = m10;
    public double M11 { get; } = m11;
    public double M12 { get; } = m12;
    public double M20 { get; } = m20;
    public double M21 { get; } = m21;
    public double M22 { get; } = m22;

    public static Matrix3x3 operator +(Matrix3x3 a, Matrix3x3 b) => new(
        a.M00 + b.M00, a.M01 + b.M01, a.M02 + b.M02,
        a.M10 + b.M10, a.M11 + b.M11, a.M12 + b.M12,
        a.M20 + b.M20, a.M21 + b.M21, a.M22 + b.M22);

    public static Matrix3x3 operator *(Matrix3x3 a, double scalar) => new(
        a.M00 * scalar, a.M01 * scalar, a.M02 * scalar,
        a.M10 * scalar, a.M11 * scalar, a.M12 * scalar,
        a.M20 * scalar, a.M21 * scalar, a.M22 * scalar);

    public static Matrix3x3 operator -(Matrix3x3 a) => a * -1;

    public static Matrix3x3 operator *(Matrix3x3 a, Matrix3x3 b) => new(
        (a.M00 * b.M00) + (a.M01 * b.M10) + (a.M02 * b.M20),
        (a.M00 * b.M01) + (a.M01 * b.M11) + (a.M02 * b.M21),
        (a.M00 * b.M02) + (a.M01 * b.M12) + (a.M02 * b.M22),
        (a.M10 * b.M00) + (a.M11 * b.M10) + (a.M12 * b.M20),
        (a.M10 * b.M01) + (a.M11 * b.M11) + (a.M12 * b.M21),
        (a.M10 * b.M02) + (a.M11 * b.M12) + (a.M12 * b.M22),
        (a.M20 * b.M00) + (a.M21 * b.M10) + (a.M22 * b.M20),
        (a.M20 * b.M01) + (a.M21 * b.M11) + (a.M22 * b.M21),
        (a.M20 * b.M02) + (a.M21 * b.M12) + (a.M22 * b.M22));

    public Matrix3x3 Transpose() => new(M00, M10, M20, M01, M11, M21, M02, M12, M22);

    public double Determinant() =>
        (M00 * ((M11 * M22) - (M12 * M21)))
        - (M01 * ((M10 * M22) - (M12 * M20)))
        + (M02 * ((M10 * M21) - (M11 * M20)));

    /// <summary>Closed-form 3x3 inverse via the adjugate/cofactor matrix.</summary>
    public Matrix3x3 Inverse()
    {
        var invDet = 1.0 / Determinant();
        var cof00 = (M11 * M22) - (M12 * M21);
        var cof01 = -((M10 * M22) - (M12 * M20));
        var cof02 = (M10 * M21) - (M11 * M20);
        var cof10 = -((M01 * M22) - (M02 * M21));
        var cof11 = (M00 * M22) - (M02 * M20);
        var cof12 = -((M00 * M21) - (M01 * M20));
        var cof20 = (M01 * M12) - (M02 * M11);
        var cof21 = -((M00 * M12) - (M02 * M10));
        var cof22 = (M00 * M11) - (M01 * M10);

        // adjugate = transpose of the cofactor matrix
        return new Matrix3x3(
            cof00 * invDet, cof10 * invDet, cof20 * invDet,
            cof01 * invDet, cof11 * invDet, cof21 * invDet,
            cof02 * invDet, cof12 * invDet, cof22 * invDet);
    }

    public double[] Multiply(double[] v) =>
    [
        (M00 * v[0]) + (M01 * v[1]) + (M02 * v[2]),
        (M10 * v[0]) + (M11 * v[1]) + (M12 * v[2]),
        (M20 * v[0]) + (M21 * v[1]) + (M22 * v[2]),
    ];

    /// <summary>The real eigenvalue/eigenvector pairs of this matrix - 1 or 3 of them, depending on
    /// how many roots of the characteristic cubic are real (a matrix with a genuine complex-conjugate
    /// pair yields just the one real eigenpair). Each eigenvector is unit-length; its sign is
    /// arbitrary (as for any eigenvector).</summary>
    public (double Value, double[] Vector)[] SolveRealEigenpairs()
    {
        var c1 = M00 + M11 + M22;
        var c2 = ((M00 * M11) - (M01 * M10))
            + ((M00 * M22) - (M02 * M20))
            + ((M11 * M22) - (M12 * M21));
        var c3 = Determinant();

        // characteristic polynomial: lambda^3 - c1*lambda^2 + c2*lambda - c3 = 0
        var eigenvalues = SolveCubicReal(-c1, c2, -c3);
        var result = new (double, double[])[eigenvalues.Length];
        for (var i = 0; i < eigenvalues.Length; i++)
        {
            result[i] = (eigenvalues[i], NullVector(eigenvalues[i]));
        }

        return result;
    }

    /// <summary>A unit-length vector in the null space of <c>(this - lambda*I)</c>, found as the
    /// largest-magnitude cross product of two of that matrix's three rows (each row is orthogonal to
    /// the null vector, so the cross product of any two independent rows spans it) - robust without
    /// needing a general-purpose SVD/rank-revealing decomposition for what is always exactly a 3x3
    /// problem here.</summary>
    private double[] NullVector(double eigenvalue)
    {
        double[] r0 = [M00 - eigenvalue, M01, M02];
        double[] r1 = [M10, M11 - eigenvalue, M12];
        double[] r2 = [M20, M21, M22 - eigenvalue];

        var candidates = new[] { Cross(r0, r1), Cross(r0, r2), Cross(r1, r2) };
        var best = candidates[0];
        var bestNormSq = NormSquared(best);
        for (var i = 1; i < candidates.Length; i++)
        {
            var normSq = NormSquared(candidates[i]);
            if (normSq > bestNormSq)
            {
                best = candidates[i];
                bestNormSq = normSq;
            }
        }

        var norm = System.Math.Sqrt(bestNormSq);
        return norm < 1e-12 ? best : [best[0] / norm, best[1] / norm, best[2] / norm];
    }

    private static double[] Cross(double[] a, double[] b) =>
    [
        (a[1] * b[2]) - (a[2] * b[1]),
        (a[2] * b[0]) - (a[0] * b[2]),
        (a[0] * b[1]) - (a[1] * b[0]),
    ];

    private static double NormSquared(double[] v) => (v[0] * v[0]) + (v[1] * v[1]) + (v[2] * v[2]);

    /// <summary>Real roots of the monic cubic <c>lambda^3 + a2*lambda^2 + a1*lambda + a0 = 0</c>, via
    /// the standard depressed-cubic substitution followed by Cardano's formula (one real root) or the
    /// trigonometric method (three real roots), chosen by the sign of the depressed cubic's
    /// discriminant.</summary>
    private static double[] SolveCubicReal(double a2, double a1, double a0)
    {
        const double epsilon = 1e-12;
        var p = a1 - (a2 * a2 / 3);
        var q = (2 * a2 * a2 * a2 / 27) - (a2 * a1 / 3) + a0;
        var shift = -a2 / 3;

        if (System.Math.Abs(p) < epsilon && System.Math.Abs(q) < epsilon)
        {
            return [shift];
        }

        var discriminant = (q * q / 4) + (p * p * p / 27);
        if (discriminant > epsilon)
        {
            var sqrtDiscriminant = System.Math.Sqrt(discriminant);
            var u = System.Math.Cbrt(-(q / 2) + sqrtDiscriminant);
            var v = System.Math.Cbrt(-(q / 2) - sqrtDiscriminant);
            return [u + v + shift];
        }

        // Three real roots: p is guaranteed < 0 here (discriminant <= epsilon forces it, barring the
        // near-zero p&q case already handled above), clamped defensively against rounding noise.
        var pNegative = System.Math.Min(p, -epsilon);
        var amplitude = 2 * System.Math.Sqrt(-pNegative / 3);
        var cos3Theta = System.Math.Clamp(3 * q / (2 * pNegative * System.Math.Sqrt(-pNegative / 3)), -1.0, 1.0);
        var theta = System.Math.Acos(cos3Theta) / 3;

        var roots = new double[3];
        for (var k = 0; k < 3; k++)
        {
            roots[k] = (amplitude * System.Math.Cos(theta - (2 * System.Math.PI * k / 3))) + shift;
        }

        return roots;
    }
}
