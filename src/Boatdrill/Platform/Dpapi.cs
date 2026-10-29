using System.Runtime.InteropServices;
using System.Text;

namespace Boatdrill.Platform;

/// <summary>
/// Repository passwords at rest, encrypted with DPAPI for the signed-in Windows user: settings.json can be
/// copied or read by anyone without giving a password away, and only this user on this PC can decrypt it.
/// Stored as "dpapi:" and base64.
/// </summary>
public static class Dpapi
{
    private const string Prefix = "dpapi:";

    // Ties the encrypted value to Boatdrill: another program running as the same user cannot decrypt it by accident.
    private static readonly byte[] Entropy = "Boatdrill repository password"u8.ToArray();

    public static string Protect(string secret)
    {
        var protectedBytes = Transform(Encoding.UTF8.GetBytes(secret), protect: true)
            ?? throw new InvalidOperationException("Windows could not encrypt the password: error " + Marshal.GetLastWin32Error());
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    /// <summary>
    /// The password, or null when there is none or it cannot be decrypted: settings copied from another user or
    /// PC. A value without the prefix was typed into settings.json by hand and is taken as it is.
    /// </summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;

        try
        {
            var plain = Transform(Convert.FromBase64String(stored[Prefix.Length..]), protect: false);
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static byte[]? Transform(byte[] input, bool protect)
    {
        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        try
        {
            var dataIn = new NativeMethods.DATA_BLOB { cbData = input.Length, pbData = inputHandle.AddrOfPinnedObject() };
            var entropy = new NativeMethods.DATA_BLOB { cbData = Entropy.Length, pbData = entropyHandle.AddrOfPinnedObject() };
            var ok = protect
                ? NativeMethods.CryptProtectData(ref dataIn, "Boatdrill", ref entropy, IntPtr.Zero, IntPtr.Zero, NativeMethods.CRYPTPROTECT_UI_FORBIDDEN, out var dataOut)
                : NativeMethods.CryptUnprotectData(ref dataIn, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, NativeMethods.CRYPTPROTECT_UI_FORBIDDEN, out dataOut);
            if (!ok) return null;

            try
            {
                var output = new byte[dataOut.cbData];
                Marshal.Copy(dataOut.pbData, output, 0, dataOut.cbData);
                return output;
            }
            finally
            {
                NativeMethods.LocalFree(dataOut.pbData);
            }
        }
        finally
        {
            Array.Clear(input);
            inputHandle.Free();
            entropyHandle.Free();
        }
    }
}
