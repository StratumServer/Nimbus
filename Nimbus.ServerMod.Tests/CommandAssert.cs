using Atlas.Api;
using Vintagestory.API.Common;
using Xunit;

namespace Nimbus.ServerMod.Tests;

/// <summary>Assertions on who refused a command, which a bare <c>Ok == false</c> plus a message
/// substring cannot tell apart.</summary>
internal static class CommandAssert
{
    /// <summary>The command's own handler refused: the engine got past command lookup and the
    /// privilege gate (a refusal there reads "nosuchcommand" or "noprivilege"), and a handler's
    /// <c>TextCommandResult.Error</c> without a code reads status Error with an empty code. A
    /// precondition that sets no code (RequiresPlayer) reads the same way, so the message
    /// fragment is what pins the handler.</summary>
    public static void RefusedByTheHandler(CommandResult result, string messageFragment)
    {
        Assert.Equal(EnumCommandStatus.Error, result.Status);
        Assert.Equal("", result.ErrorCode);
        Assert.Contains(messageFragment, result.Message);
    }
}
