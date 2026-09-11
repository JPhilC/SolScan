using SolScan.Core.Processing;

namespace SolScan.Processing.Shg;

/// <summary>One output image from <see cref="IShgProcessor"/> - already scaled to a 16-bit container
/// (0-65535), ready to save. Pure in-memory data, no file IO here - see <see cref="ShgProcessor"/>'s
/// own doc comment for why the actual PNG encoding happens in <c>SolScan.App</c> instead.</summary>
public sealed record ProcessedImage(GeneratedImageKind Kind, int Width, int Height, ushort[,] Pixels);
