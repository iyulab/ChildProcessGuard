#if NET10_0_OR_GREATER
using AwesomeAssertions;
using Xunit;

namespace ChildProcessGuard.Tests;

/// <summary>
/// Tests for the options side of <see cref="ProcessGuardianOptions.CleanupOnSigterm"/>. A real owner
/// receiving SIGTERM is covered by <see cref="OwnerDeathTests"/>.
/// </summary>
public class CleanupOnSigtermTests
{
    [Fact]
    [Trait("Category", "Fast")]
    public void Option_IsOffByDefault()
    {
        new ProcessGuardianOptions().CleanupOnSigterm.Should().BeFalse();
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Clone_CopiesOption()
    {
        var options = new ProcessGuardianOptions { CleanupOnSigterm = true };

        options.Clone().CleanupOnSigterm.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void Builder_SetsOption()
    {
        using var guardian = new ProcessGuardianBuilder().WithCleanupOnSigterm().Build();

        guardian.Options.CleanupOnSigterm.Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Fast")]
    public void GuardianWithTheOption_CanBeDisposedTwiceAndAfterItsRegistration()
    {
        // On Windows the option is accepted and ignored; on Unix it registers and Dispose unregisters
        var guardian = new ProcessGuardianBuilder().WithCleanupOnSigterm().Build();

        guardian.Dispose();
        guardian.Invoking(g => g.Dispose()).Should().NotThrow();
        guardian.IsDisposed.Should().BeTrue();
    }
}
#endif
