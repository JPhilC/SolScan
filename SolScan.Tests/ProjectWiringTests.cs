using SolScan.Core.Telescope;

namespace SolScan.Tests;

/// <summary>Smoke test proving the project references are wired correctly - replace/extend as
/// real algorithms land in SolScan.Processing.</summary>
public class ProjectWiringTests
{
    [Fact]
    public void CoreAssembly_ExposesTelescopeMountContract()
    {
        Assert.True(typeof(ITelescopeMount).IsInterface);
    }
}
