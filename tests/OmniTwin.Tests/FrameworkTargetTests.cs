using System.Reflection;
using System.Runtime.Versioning;
using OmniTwin.Contracts;
using OmniTwin.Perception.Calibration;
using OmniTwin.Transport;
using Xunit;

namespace OmniTwin.Tests;

public sealed class FrameworkTargetTests
{
    [Fact]
    public void ExecutesTheIntendedDomainBinaries()
    {
#if NETSTANDARD_BINARIES
        const string expected = ".NETStandard,Version=v2.1";
#else
        const string expected = ".NETCoreApp,Version=v10.0";
#endif
        Assert.Equal(expected, typeof(Vector2D).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
        Assert.Equal(expected, typeof(HomographyMapper).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
        Assert.Equal(expected, typeof(WorldStreamCodec).Assembly.GetCustomAttribute<TargetFrameworkAttribute>()?.FrameworkName);
    }
}
