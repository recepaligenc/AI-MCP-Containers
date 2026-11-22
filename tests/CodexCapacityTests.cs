using AiMcpContainers;
using Xunit;

namespace AiMcpContainers.Tests;

public class CodexCapacityTests
{
    [Theory]
    [InlineData("{\"type\":\"error\",\"message\":\"Selected model is at capacity. Please try a different model.\"}", true)]
    [InlineData("{\"type\":\"turn.failed\",\"error\":{\"message\":\"Selected model is at capacity. Please try a different model.\"}}", true)]
    [InlineData("{\"type\":\"item.completed\",\"message\":\"Selected model is at capacity.\"}", false)]
    [InlineData("Selected model is at capacity.", false)]
    [InlineData("{\"type\":\"error\",\"message\":\"Authentication failed\"}", false)]
    [InlineData("null\n{\"type\":\"turn.completed\"}", false)]
    public void OnlyStructuredCapacityErrorsAreClassified(string stdout, bool expected)
        => Assert.Equal(expected, CodexRunner.HasModelCapacityError(stdout));
}
