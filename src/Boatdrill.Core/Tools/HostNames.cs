namespace Boatdrill.Core.Tools;

/// <summary>Host names as backup tools record them in snapshots, matched against this PC's.</summary>
internal static class HostNames
{
    /// <summary>The longest NetBIOS name: Windows cuts a longer DNS host name to this many characters.</summary>
    private const int NetBiosLength = 15;

    /// <summary>
    /// Whether a snapshot recorded under this host name was taken on this PC. Go programs record the DNS host name
    /// (kopia lower-cases it and cuts it at the first dot); .NET's machine name is the 15-character NetBIOS form of
    /// it and, on Linux, stops at the first dot.
    /// </summary>
    public static bool IsThisPc(string hostname) => Matches(hostname, Environment.MachineName);

    /// <summary>
    /// The rule, for any machine name. A machine name of exactly fifteen characters may be a longer DNS name cut
    /// short, so a recorded name that begins with it counts as this PC's. The names Windows makes up itself
    /// (DESKTOP-4QK7M2P) are fifteen characters too, and nothing after the fifteenth can tell such a PC from another
    /// whose name begins the same way: that one is taken for this PC, which only ever breaks a tie between snapshots.
    /// </summary>
    internal static bool Matches(string hostname, string machineName)
    {
        var host = hostname.Split('.')[0];
        return string.Equals(host, machineName, StringComparison.OrdinalIgnoreCase) ||
               (machineName.Length == NetBiosLength && host.StartsWith(machineName, StringComparison.OrdinalIgnoreCase));
    }
}
