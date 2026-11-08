using System.Text.Json;
using SimpleFile.PluginHost;
using SimpleFile.PluginHost.Manifest;
using Xunit;

namespace SimpleFile.Tests;

public class PluginSettingsSchemaTests
{
    private static PluginSettingSpec Spec(string type, string? defaultJson = null) => new(
        "key",
        type,
        "Label",
        null,
        defaultJson is null ? null : JsonDocument.Parse(defaultJson).RootElement.Clone(),
        null,
        type == "enum" ? ["a", "b"] : null);

    [Theory]
    [InlineData("integer", "12", null)]
    [InlineData("integer", "x", "whole number")]
    [InlineData("boolean", "maybe", "true or false")]
    [InlineData("enum", "c", "one of")]
    [InlineData("boolean", "true", null)]
    [InlineData("enum", "b", null)]
    [InlineData("string", "anything", null)]
    [InlineData("path", @"C:\Temp", null)]
    public void ValidateValue_Cases(string type, string value, string? expectedFragment)
    {
        var message = PluginSettingsSchema.ValidateValue(Spec(type), value);

        if (expectedFragment is null)
        {
            Assert.Null(message);
        }
        else
        {
            Assert.NotNull(message);
            Assert.Contains(expectedFragment, message);
        }
    }

    [Fact]
    public void DefaultValue_UsesSpecDefaultOrTypeDefault()
    {
        Assert.Equal("Hello", PluginSettingsSchema.DefaultValue(Spec("string", "\"Hello\"")));
        Assert.Equal("true", PluginSettingsSchema.DefaultValue(Spec("boolean", "true")));
        Assert.Equal("42", PluginSettingsSchema.DefaultValue(Spec("integer", "42")));
        Assert.Equal("b", PluginSettingsSchema.DefaultValue(Spec("enum", "\"b\"")));

        Assert.Equal("", PluginSettingsSchema.DefaultValue(Spec("string")));
        Assert.Equal("", PluginSettingsSchema.DefaultValue(Spec("path")));
        Assert.Equal("false", PluginSettingsSchema.DefaultValue(Spec("boolean")));
        Assert.Equal("0", PluginSettingsSchema.DefaultValue(Spec("integer")));
        Assert.Equal("a", PluginSettingsSchema.DefaultValue(Spec("enum")));
    }
}
