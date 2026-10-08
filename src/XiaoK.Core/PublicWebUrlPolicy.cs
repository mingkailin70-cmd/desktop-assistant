using System.Net;
using System.Net.Sockets;

namespace XiaoK.Core;

/// <summary>公开网页读取使用的 HTTPS 与公网地址限制。</summary>
public static class PublicWebUrlPolicy
{
    public const int MaximumUrlLength = 2_048;

    public static bool IsAllowedUrlShape(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumUrlLength
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return IsAllowedUriShape(uri);
    }

    public static bool IsAllowedUriShape(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps
            || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.HostNameType != UriHostNameType.Dns)
            return false;

        var host = uri.IdnHost;
        if (host.Length is < 4 or > 253 || host.EndsWith(".", StringComparison.Ordinal)
            || !host.Contains(".", StringComparison.Ordinal)) return false;

        return !host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".test", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".example", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase)
            && !host.EndsWith(".onion", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<IPAddress[]> ResolvePublicAddressesAsync(string host, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false); }
        catch (SocketException ex) { throw new PublicWebHostResolutionException("无法解析网页主机名。", ex); }

        if (addresses.Length == 0 || addresses.Any(address => !IsPubliclyRoutable(address)))
            throw new PublicWebAddressException("网页主机名解析到了非公网地址；已阻止连接。");
        return addresses;
    }

    public static bool IsPubliclyRoutable(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPubliclyRoutable(address.MapToIPv4());
        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var a = bytes[0];
            var b = bytes[1];
            if (a is 0 or 10 or 127 || a >= 224) return false;
            if (a == 100 && (b & 0xC0) == 0x40) return false; // shared address space 100.64/10
            if (a == 169 && b == 254 || a == 172 && b is >= 16 and <= 31
                || a == 192 && b == 168) return false;
            if (a == 192 && b == 0 && bytes[2] == 0 || a == 192 && b == 0 && bytes[2] == 2
                || a == 192 && b == 88 && bytes[2] == 99 || a == 198 && b is 18 or 19
                || a == 198 && b == 51 && bytes[2] == 100
                || a == 203 && b == 0 && bytes[2] == 113) return false;
            if (a == 255) return false;
            return true;
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 || IPAddress.IPv6Any.Equals(address)
            || IPAddress.IPv6Loopback.Equals(address) || address.IsIPv6LinkLocal || address.IsIPv6Multicast
            || (bytes[0] & 0xE0) != 0x20) return false;

        // Only global-unicast space is eligible. Exclude IETF special-purpose,
        // documentation, Teredo, 6to4, and the newer 3fff::/20 documentation range.
        return !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] <= 0x01
            || bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0D && bytes[3] == 0xB8
            || bytes[0] == 0x20 && bytes[1] == 0x02
            || bytes[0] == 0x3F && bytes[1] == 0xFF);
    }
}

public sealed class PublicWebAddressException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class PublicWebHostResolutionException(string message, Exception? innerException = null)
    : Exception(message, innerException);
