using Xunit;

namespace Maptek.Vulcan.Stp.Bridge.Tests
{
    public class SkeletonTests
    {
        [Fact]
        public void BridgeAssemblyLoads()
        {
            Assert.Equal("X-STP-V2-Token", BridgeInfo.TokenHeader);
        }
    }
}
