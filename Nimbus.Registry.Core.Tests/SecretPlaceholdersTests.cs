using Nimbus.Shared;
using Xunit;

namespace Nimbus.Registry.Core.Tests;

/// <summary>
/// The list of values that mean "nobody has chosen a secret here yet". It is shared because the
/// proxy validator, the standalone registry's boot warning and the backend mod's configured check
/// all have to agree: a literal one component refuses and another accepts is a network held
/// together by a credential one third of it considers unset.
/// </summary>
public class SecretPlaceholdersTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("change-me-and-keep-secret")]
    [InlineData("REPLACE_ME_WITH_A_LONG_RANDOM_STRING")]
    [InlineData(SecretPlaceholders.Egg)]
    [InlineData(SecretPlaceholders.BackendConfig)]
    public void EveryLiteralNimbusHasEverShipped_CountsAsUnset(string secret)
    {
        // Nothing is ever dropped from this list: an operator upgrading from an older release
        // still has whatever literal that release wrote sitting in their config file.
        Assert.True(SecretPlaceholders.IsPlaceholder(secret));
    }

    [Fact]
    public void ANullSecret_CountsAsUnsetRatherThanThrowing()
    {
        // Deserializing a config whose key is missing is how this arrives.
        Assert.True(SecretPlaceholders.IsPlaceholder(null));
    }

    [Fact]
    public void AGeneratedSecret_NeverCountsAsUnset()
    {
        // The other half of the first-run contract: a component that generated a secret must not
        // then warn about it, or refuse to serve on it. The alphabet and length are the
        // generator's business, but this is the assertion that ties the two together.
        for (int i = 0; i < 50; i++)
            Assert.False(SecretPlaceholders.IsPlaceholder(SecretGenerator.NewSharedSecret()));
    }

    [Fact]
    public void ARealSecret_IsNotRejectedForLookingLikeOne()
    {
        Assert.False(SecretPlaceholders.IsPlaceholder("change-me-and-keep-secret-but-longer"));
        Assert.False(SecretPlaceholders.IsPlaceholder("CHANGE-ME-AND-KEEP-SECRET"));
    }
}
