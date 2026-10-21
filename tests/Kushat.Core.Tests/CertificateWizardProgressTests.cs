using Kushat.Core;
using Xunit;

namespace Kushat.Core.Tests;

public class CertificateWizardProgressTests
{
    static readonly Uri Endpoint = new("https://127.0.0.1:8010");
    static ConnectionReport Report(string fingerprint = "new", bool ready = true, bool port = true) =>
        new(Endpoint, port, ready, "Test", new([], "CN=test", "CN=test", fingerprint, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow.AddDays(10)));

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void CannotContinueUntilServiceProcessAndPortAreAllStopped(bool serviceStopped, bool processRunning, bool portOpen)
    {
        var state = new CertificateWizardProgress(); state.Prepared("new");
        state.ObserveStopped(serviceStopped, processRunning, portOpen);
        Assert.False(state.CanContinue);
        Assert.Throws<InvalidOperationException>(state.Installed);
    }

    [Fact]
    public void EntireWizardRequiresStoppedStateAndVerifiedNewCertificateBeforeFinish()
    {
        var state = new CertificateWizardProgress();
        Assert.Equal(CertificateWizardStep.Inspect, state.Step);
        Assert.Throws<InvalidOperationException>(state.Complete);
        state.Prepared("new"); state.ObserveStopped(true, false, false);
        Assert.True(state.CanContinue);
        state.Installed(); Assert.False(state.CanContinue);
        state.ObserveStarted(true, true, Report("old")); Assert.False(state.CanContinue);
        state.ObserveStarted(true, true, Report()); Assert.True(state.CanContinue);
        state.Complete(); Assert.Equal(CertificateWizardStep.Complete, state.Step);
    }

    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void RunningFlagAloneCannotEnableContinue(bool service, bool process, bool trusted, bool port)
    {
        var state = new CertificateWizardProgress(); state.Prepared("new");
        state.ObserveStopped(true, false, false); state.Installed();
        state.ObserveStarted(service, process, Report(ready: trusted, port: port));
        Assert.False(state.CanContinue); Assert.Throws<InvalidOperationException>(state.Complete);
    }

    [Fact]
    public void RollbackCanConfirmOriginalCertificateWithoutClaimingItIsValid()
    {
        var state = new CertificateWizardProgress(); state.ExpectRollback("old");
        state.ObserveStarted(true, true, Report("different", false)); Assert.False(state.CanContinue);
        state.ObserveStarted(true, true, Report("old", false)); Assert.True(state.CanContinue);
        Assert.True(state.RolledBack);
    }

    [Fact]
    public void ExistingInvalidCertificateCannotBeMarkedComplete()
    {
        var state = new CertificateWizardProgress();
        Assert.Throws<InvalidOperationException>(() => state.CompleteExisting(Report(ready: false)));
        state.CompleteExisting(Report()); Assert.Equal(CertificateWizardStep.Complete, state.Step);
    }
}
