using SolScan.Core.Processing;

namespace SolScan.Processing.Shg;

/// <summary>The colour counterpart to <see cref="ProcessedImage"/> - currently only ever produced for
/// <see cref="GeneratedImageKind.Colorized"/>. Kept as its own type rather than making
/// <see cref="ProcessedImage.Pixels"/> nullable-plus-an-optional-colour-payload: every existing
/// consumer (PNG saving, disk-based preview loading) already branches on "is this image mono or
/// colour" by file content anyway, so two small, single-purpose types stay simpler than one type that
/// can be in either shape.</summary>
public sealed record ProcessedColorImage(GeneratedImageKind Kind, int Width, int Height, ushort[,] R, ushort[,] G, ushort[,] B);
