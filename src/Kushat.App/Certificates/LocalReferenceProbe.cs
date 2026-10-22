using Kushat.Core;

namespace Kushat.App;

public static class LocalReferenceProbe
{
    public static async Task<ConnectionReport?> FindAsync(LocalReferenceServer server, string? expectedFingerprint,
        CancellationToken cancellationToken)
    {
        ConnectionReport? first = null;
        foreach (var endpoint in WindowsReferenceDiscovery.HttpsCandidates(server).Take(16))
        {
            var report = await new ConnectionPreflight().CheckAsync(endpoint, 2, cancellationToken);
            if (report.Certificate is null) continue;
            first ??= report;
            if (expectedFingerprint is null || report.Certificate.Sha256 == expectedFingerprint) return report;
        }
        return first;
    }
}
