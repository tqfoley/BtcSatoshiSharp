using SatoshiSharpLib;

namespace SatoshiSharpTest;

public class UnitTest1
{
    [Fact]
    public void Test1()
    {

        SatoshiSharpLib.Wallet w = new SatoshiSharpLib.Wallet();
        w.g = 6;
        Assert.Equal(6, w.g);

    }
}
