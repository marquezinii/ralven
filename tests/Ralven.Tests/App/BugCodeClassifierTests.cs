using Ralven.App.Services;
using Ralven.Windows.Diagnostics;
using Ralven.Contracts;
using Xunit;

namespace Ralven.Tests.App;

public sealed class BugCodeClassifierTests
{
    [Fact]
    public void ClassifyOptimizationException_CoversPowerAndAppearanceActions()
    {
        var exception = new InvalidOperationException();

        Assert.Equal(
            BugCode.WIN_POWER_PLAN,
            BugCodeClassifier.ClassifyOptimizationException(
                exception,
                "windows.power.performance-session.enable"));
        Assert.Equal(
            BugCode.WIN_DISPLAY_CONFIG,
            BugCodeClassifier.ClassifyOptimizationException(
                exception,
                "windows.appearance.visual-effects.reduce"));
    }

    [Fact]
    public void ClassifyBrokerException_DistinguishesIntegrityAndTerminalFailures()
    {
        Assert.Equal(
            BugCode.BRK_INTEGRITY_VALIDATION,
            BugCodeClassifier.ClassifyBrokerException(
                new BrokerIntegrityException(new InvalidDataException())));
        Assert.Equal(
            BugCode.BRK_TRANSACTION_INCOMPLETE,
            BugCodeClassifier.ClassifyBrokerFailure("transaction-not-committed", wasCancelled: false));
        Assert.Equal(
            BugCode.BRK_REQUEST_VALIDATION,
            BugCodeClassifier.ClassifyBrokerFailure("plan-expired", wasCancelled: false));
        Assert.Equal(
            BugCode.BRK_IPC_COMMUNICATION,
            BugCodeClassifier.ClassifyBrokerFailure("broker-pipe-connection-failed", wasCancelled: false));
    }
}
