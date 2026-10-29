namespace Boatdrill.Core.Tools;

/// <summary>Host names as backup tools record them in snapshots, matched against this PC's.</summary>
internal static class HostNames
{
    /// <summary>
    /// Whether a snapshot recorded under this host name was taken on this PC. Go programs record the DNS host name
    /// (kopia lower-cases it and cuts it at the first dot); .NET's machine name is the 15-character NetBIOS form of
    /// it and, on Linux, stops at the first dot.
    /// </summary>
    public static bool IsThisPc(string hostname)
    {
        var host = hostname.Split('.')[0];
        var here = Environment.MachineName;
        return string.Equals(host, here, StringComparison.OrdinalIgnoreCase) ||
               (here.Length == 15 && host.StartsWith(here, StringComparison.OrdinalIgnoreCase));
    }
}
