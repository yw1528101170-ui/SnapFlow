namespace Jietu.Tests;

public sealed class HotkeyGestureTests
{
    [Fact]
    public void Parse_AltA_ProducesDisplayAndCode()
    {
        Assert.True(HotkeyGesture.TryParse("Alt+A", out var gesture, out var error));
        Assert.Null(error);
        Assert.Equal("Alt+A", gesture.Display);
        Assert.Equal(HotkeyGesture.ModAlt, gesture.Modifiers);
        Assert.Equal(0x41u, gesture.VirtualKey);
    }

    [Fact]
    public void Parse_ToleratesSpacesAndCase()
    {
        Assert.True(HotkeyGesture.TryParse("alt + a", out var gesture, out _));
        Assert.Equal("Alt+A", gesture.Display);
    }

    [Fact]
    public void Parse_CtrlShiftDigit()
    {
        Assert.True(HotkeyGesture.TryParse("Ctrl+Shift+1", out var gesture, out _));
        Assert.Equal(HotkeyGesture.ModControl | HotkeyGesture.ModShift, gesture.Modifiers);
        Assert.Equal('1', (char)gesture.VirtualKey);
        Assert.Equal("Ctrl+Shift+1", gesture.Display);
    }

    [Theory]
    [InlineData("A")]        // 没有修饰键
    [InlineData("Shift+A")]  // 只有 Shift 不算有效修饰
    [InlineData("Ctrl+你好")] // 无法识别按键
    [InlineData("Foo+A")]    // 无法识别修饰键
    [InlineData("")]
    public void Parse_RejectsInvalid(string input)
    {
        Assert.False(HotkeyGesture.TryParse(input, out _, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Format_TryParse_RoundTripNamedKey()
    {
        var display = HotkeyGesture.Format(HotkeyGesture.ModControl, 0x20);
        Assert.Equal("Ctrl+Space", display);
        Assert.True(HotkeyGesture.TryParse(display, out var gesture, out _));
        Assert.Equal(0x20u, gesture.VirtualKey);
        Assert.Equal(display, gesture.Display);
    }

    [Fact]
    public void Parse_LegacyHexKey()
    {
        // 回归：旧版本曾把未命名键写成 "Ctrl+0x20"，解析器必须继续读得回。
        Assert.True(HotkeyGesture.TryParse("Ctrl+0x20", out var gesture, out _));
        Assert.Equal(0x20u, gesture.VirtualKey);
    }

    [Fact]
    public void Parse_FunctionKey()
    {
        Assert.True(HotkeyGesture.TryParse("Alt+F3", out var gesture, out _));
        Assert.Equal(0x72u, gesture.VirtualKey);
        Assert.Equal("Alt+F3", gesture.Display);
    }

    [Fact]
    public void DuplicateMessage_FlagsDuplicatesOnly()
    {
        HotkeyGesture.TryParse("Alt+A", out var a, out _);
        HotkeyGesture.TryParse("Alt+V", out var v, out _);
        Assert.Null(HotkeyGesture.DuplicateMessage([a, v]));
        Assert.NotNull(HotkeyGesture.DuplicateMessage([a, v, a]));
    }
}
