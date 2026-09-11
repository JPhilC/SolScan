using SolScan.Core.Processing;

namespace SolScan.Tests;

public class ProcessingLocationsTests
{
    [Fact]
    public void GetOutputFolder_ResolvesToASiblingFolderNamedAfterTheSerFile()
    {
        var serFilePath = Path.Combine("C:", "Raw", "SolScan", "20250831", "SolScan_20250831_161427.ser");

        var outputFolder = ProcessingLocations.GetOutputFolder(serFilePath);

        Assert.Equal(
            Path.Combine("C:", "Raw", "SolScan", "20250831", "SolScan_20250831_161427"),
            outputFolder);
    }

    [Fact]
    public void GetOutputFolder_WithNoDirectoryComponent_ReturnsJustTheBaseName()
    {
        Assert.Equal("SolScan_20250831_161427", ProcessingLocations.GetOutputFolder("SolScan_20250831_161427.ser"));
    }

    [Theory]
    [InlineData(DirectoryKind.Raw, "raw")]
    [InlineData(DirectoryKind.Processed, "processed")]
    [InlineData(DirectoryKind.Debug, "debug")]
    [InlineData(DirectoryKind.Custom, "custom")]
    public void GetImagesFolder_IsALowercasedSubfolderOfTheOutputFolder(DirectoryKind directoryKind, string expectedSubfolder)
    {
        var serFilePath = Path.Combine("C:", "Raw", "SolScan", "20250831", "SolScan_20250831_161427.ser");

        var imagesFolder = ProcessingLocations.GetImagesFolder(serFilePath, directoryKind);

        Assert.Equal(
            Path.Combine("C:", "Raw", "SolScan", "20250831", "SolScan_20250831_161427", expectedSubfolder),
            imagesFolder);
    }

    [Theory]
    [InlineData(GeneratedImageKind.Raw, DirectoryKind.Raw)]
    [InlineData(GeneratedImageKind.Reconstruction, DirectoryKind.Raw)]
    [InlineData(GeneratedImageKind.Continuum, DirectoryKind.Processed)]
    [InlineData(GeneratedImageKind.GeometryCorrected, DirectoryKind.Processed)]
    [InlineData(GeneratedImageKind.GeometryCorrectedProcessed, DirectoryKind.Processed)]
    public void GetDirectoryKind_MatchesRealJSolexMapping(GeneratedImageKind kind, DirectoryKind expected)
    {
        Assert.Equal(expected, kind.GetDirectoryKind());
    }
}
