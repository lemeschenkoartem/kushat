namespace Kushat.Core;

public enum CertificateWizardStep { Inspect, StopServer, StartServer, Complete }

public sealed class CertificateWizardProgress
{
    public CertificateWizardStep Step { get; private set; }
    public bool CanContinue { get; private set; }
    public bool CertificateInstalled { get; private set; }
    public bool CanGoBack => Step != CertificateWizardStep.Inspect;
    public bool RolledBack { get; private set; }
    public string ExpectedFingerprint { get; private set; } = "";

    public void Prepared(string fingerprint)
    {
        if (Step != CertificateWizardStep.Inspect || CertificateInstalled || string.IsNullOrWhiteSpace(fingerprint)) throw new InvalidOperationException("Неверный шаг подготовки.");
        ExpectedFingerprint = fingerprint; RolledBack = false; Step = CertificateWizardStep.StopServer; CanContinue = false;
    }
    public void ObserveStopped(bool serviceStopped, bool processRunning, bool portOpen) =>
        CanContinue = Step == CertificateWizardStep.StopServer && serviceStopped && !processRunning && !portOpen;
    public void Installed()
    {
        if (Step != CertificateWizardStep.StopServer || !CanContinue) throw new InvalidOperationException("Сервер ещё не остановлен.");
        CertificateInstalled = true; Step = CertificateWizardStep.StartServer; CanContinue = false;
    }
    public void ExpectRollback(string fingerprint)
    {
        CertificateInstalled = false; ExpectedFingerprint = fingerprint; RolledBack = true; Step = CertificateWizardStep.StartServer; CanContinue = false;
    }
    public void ObserveStarted(bool serviceRunning, bool processRunning, ConnectionReport report) =>
        CanContinue = Step == CertificateWizardStep.StartServer && serviceRunning && processRunning &&
            report.PortOpen && (report.Ready || RolledBack) && report.Certificate?.Sha256 == ExpectedFingerprint;
    public void Complete()
    {
        if (!CanContinue || Step != CertificateWizardStep.StartServer) throw new InvalidOperationException("Проверка запуска ещё не завершена.");
        Step = CertificateWizardStep.Complete; CanContinue = false;
    }
    public void Back()
    {
        Step = Step switch
        {
            CertificateWizardStep.Complete => CertificateInstalled || RolledBack ? CertificateWizardStep.StartServer : CertificateWizardStep.Inspect,
            CertificateWizardStep.StartServer => RolledBack ? CertificateWizardStep.Inspect : CertificateWizardStep.StopServer,
            CertificateWizardStep.StopServer => CertificateWizardStep.Inspect,
            _ => throw new InvalidOperationException("Это первый шаг мастера.")
        };
        CanContinue = false;
    }
    public void ContinueInstalled()
    {
        if (!CertificateInstalled || Step is not (CertificateWizardStep.Inspect or CertificateWizardStep.StopServer))
            throw new InvalidOperationException("Установка сертификата ещё не завершена.");
        Step = Step == CertificateWizardStep.Inspect ? CertificateWizardStep.StopServer : CertificateWizardStep.StartServer;
        CanContinue = false;
    }
    public void CompleteLocalCertificate(DateTime validFromUtc, DateTime validUntilUtc, DateTime nowUtc)
    {
        if (Step != CertificateWizardStep.Inspect || CertificateInstalled || validFromUtc > nowUtc || nowUtc >= validUntilUtc)
            throw new InvalidOperationException("Срок сертификата недействителен. Выпустите новый сертификат.");
        Step = CertificateWizardStep.Complete; CanContinue = false;
    }
    public void CompleteExisting(ConnectionReport report)
    {
        if (Step != CertificateWizardStep.Inspect || !report.Ready) throw new InvalidOperationException("Сертификат ещё не проверен.");
        Step = CertificateWizardStep.Complete;
    }
}
