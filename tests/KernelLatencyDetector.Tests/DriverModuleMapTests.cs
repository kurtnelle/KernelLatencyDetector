using KernelLatencyDetector;
using Xunit;

public class DriverModuleMapTests
{
    private static DriverModuleMap TwoModuleMap()
    {
        var map = new DriverModuleMap();
        map.Add(0x1000, 0x100, "a.sys"); // [0x1000, 0x1100)
        map.Add(0x2000, 0x080, "b.sys"); // [0x2000, 0x2080)
        return map;
    }

    [Fact]
    public void Resolve_AddressAtBase_ReturnsModule()
        => Assert.Equal("a.sys", TwoModuleMap().Resolve(0x1000));

    [Fact]
    public void Resolve_AddressAtLastByte_ReturnsModule()
        => Assert.Equal("a.sys", TwoModuleMap().Resolve(0x10FF));

    [Fact]
    public void Resolve_AddressAtEndExclusive_ReturnsNull()
        => Assert.Null(TwoModuleMap().Resolve(0x1100));

    [Fact]
    public void Resolve_AddressInSecondModule_ReturnsSecond()
        => Assert.Equal("b.sys", TwoModuleMap().Resolve(0x2040));

    [Fact]
    public void Resolve_AddressBelowAll_ReturnsNull()
        => Assert.Null(TwoModuleMap().Resolve(0x0500));

    [Fact]
    public void Resolve_AddressInGapBetweenModules_ReturnsNull()
        => Assert.Null(TwoModuleMap().Resolve(0x1800));

    [Fact]
    public void Add_ZeroSize_IsIgnored()
    {
        var map = new DriverModuleMap();
        map.Add(0x5000, 0, "z.sys");
        Assert.Null(map.Resolve(0x5000));
    }
}
