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
    [Theory]
    [InlineData(-10, -1)]
    [InlineData(-10, 0)]
    [InlineData(1, 10)]
    public void ExpiredOrNotYetValidLocalCertificateCannotReachFinish(int fromDays, int untilDays)
    {
        var now = DateTime.UtcNow;
        var state = new CertificateWizardProgress();
        Assert.Throws<InvalidOperationException>(() => state.CompleteLocalCertificate(now.AddDays(fromDays), now.AddDays(untilDays), now));
        Assert.Equal(CertificateWizardStep.Inspect, state.Step);
    }

    [Fact]
    public void ValidLocalCertificateCanFinishWithoutEnteringAnAddressOrPort()
    {
        var now = DateTime.UtcNow; var state = new CertificateWizardProgress();
        state.CompleteLocalCertificate(now.AddDays(-1), now.AddDays(1), now);
        Assert.Equal(CertificateWizardStep.Complete, state.Step);
        state.Back(); Assert.Equal(CertificateWizardStep.Inspect, state.Step);
    }

    [Fact]
    public void BackAfterInstallationPreservesRecordedWorkAndCannotFinishWithoutRecheckingLaunch()
    {
        var state = new CertificateWizardProgress();
        Assert.False(state.CanGoBack); Assert.Throws<InvalidOperationException>(state.Back);
        state.Prepared("new"); state.ObserveStopped(true, false, false); state.Installed();
        state.Back(); Assert.Equal(CertificateWizardStep.StopServer, state.Step); Assert.True(state.CertificateInstalled);
        state.Back(); Assert.Equal(CertificateWizardStep.Inspect, state.Step);
        Assert.Throws<InvalidOperationException>(() => state.Prepared("different"));
        state.ContinueInstalled(); Assert.Equal(CertificateWizardStep.StopServer, state.Step);
        state.ContinueInstalled(); Assert.Equal(CertificateWizardStep.StartServer, state.Step);
        Assert.Throws<InvalidOperationException>(state.Complete);
        state.ObserveStarted(true, true, Report()); state.Complete(); state.Back();
        Assert.Equal(CertificateWizardStep.StartServer, state.Step); Assert.False(state.CanContinue);
        Assert.Throws<InvalidOperationException>(state.Complete);
    }

    [Fact]
    public void BackBeforeInstallationDoesNotRecordAnyCertificateChange()
    {
        var state = new CertificateWizardProgress(); state.Prepared("first"); state.Back();
        Assert.False(state.CertificateInstalled); Assert.Equal(CertificateWizardStep.Inspect, state.Step);
        Assert.Throws<InvalidOperationException>(state.ContinueInstalled);
        state.Prepared("second"); Assert.Equal("second", state.ExpectedFingerprint);
    }

}
