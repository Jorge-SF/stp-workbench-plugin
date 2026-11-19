using Xunit;

namespace Maptek.Vulcan.Stp.Engine.Tests
{
    public class SkeletonTests
    {
        [Fact]
        public void EngineAssemblyLoads()
        {
            Assert.Equal("reference/web/planner_logic.js", EngineInfo.ReferenceSource);
        }
    }
}
