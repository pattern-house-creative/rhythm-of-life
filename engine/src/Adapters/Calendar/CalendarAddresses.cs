using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Knowledge.Adapters.Calendar;

// Text a feed wrote, without what only serves to hide words from a reader, as anyone who can send an invitation can write it:
// control and format characters (the Unicode tag characters among them, which a model reads and a person never sees), line and
// paragraph separators as line ends, private use, and every code point Unicode says a display may ignore. It walks whole
// characters, as half of a pair says nothing of what the pair is.
internal static class CalendarText
{
    public static string Visible(string text)
    {
        var kept = new StringBuilder(text.Length);
        foreach (var rune in text.Replace("\r\n", "\n").EnumerateRunes())
        {
            if (rune.Value is '\n' or '\t') kept.Append((char)rune.Value);
            else if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) kept.Append('\n');
            else if (!Hidden(rune)) kept.Append(rune.ToString());
        }
        return kept.ToString();
    }

    static bool Hidden(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.PrivateUse
        // Unicode's Default_Ignorable_Code_Point that are not Format: fillers, joiners and variation selectors, and the tag block's whole plane range.
        || rune.Value is 0x034F or 0x115F or 0x1160 or 0x17B4 or 0x17B5 or 0x3164 or 0xFFA0
            or (>= 0x180B and <= 0x180F) or (>= 0xFE00 and <= 0xFE0F) or (>= 0xFFF0 and <= 0xFFF8) or (>= 0xE0000 and <= 0xE0FFF);
}

// A redirect's address, refused because it is not on the public internet. Words of our own, naming nothing.
internal sealed class CalendarAddressRefused() : IOException("not a public address");

// Where a calendar server's redirect may lead: only the public internet. The owner's own link may name their own network, as
// they chose it; a server's redirect may not, or a feed's server could turn the engine on what is behind it. This is the
// calendar's own check and not NightscoutAdapter.Local, which decides where plain http may go and answers another question.
internal static class CalendarAddresses
{
    // The names that never resolve on the public internet: mDNS's, the special-use ones (RFC 6761, RFC 8375, ICANN's .internal)
    // and those RFC 6762's appendix G found in private use.
    static readonly string[] Private = [".localhost", ".local", ".home.arpa", ".internal", ".intranet", ".private", ".corp", ".home", ".lan"];

    // Whether a redirect's address reads as public by itself: an address that is, or a name of two or more labels in no private
    // suffix. A name's real address is checked when connecting (Connect). No trailing dot: a redirect has no need of one, and
    // it is how a short name escapes a search on its shape.
    public static bool PublicName(Uri uri) => uri.HostNameType switch
    {
        UriHostNameType.Dns => NameIsPublic(uri.IdnHost),
        UriHostNameType.IPv4 or UriHostNameType.IPv6 => IPAddress.TryParse(uri.DnsSafeHost, out var ip) && Public(ip),
        _ => false,
    };

    static bool NameIsPublic(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return false;
        if (name[(dot + 1)..].All(char.IsAsciiDigit)) return false;
        return !Private.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    // Whether an address is on the public internet. IPv6 is public only in global unicast (2000::/3), less the ranges inside it
    // that are not; one that carries an IPv4 address (mapped, NAT64, 6to4) is what that address is.
    public static bool Public(IPAddress ip)
    {
        var b = (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).GetAddressBytes();
        if (b.Length == 4) return PublicV4(b);
        if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.AsSpan(4, 8).IndexOfAnyExcept((byte)0) < 0) return PublicV4(b.AsSpan(12, 4));
        if ((b[0] & 0xE0) != 0x20) return false;
        if (b[0] == 0x20 && b[1] == 0x02) return PublicV4(b.AsSpan(2, 4));
        // Teredo (2001::/32) and documentation's (2001:db8::/32).
        return !(b[0] == 0x20 && b[1] == 0x01 && (b[2] == 0 && b[3] == 0 || b[2] == 0x0D && b[3] == 0xB8));
    }

    static bool PublicV4(ReadOnlySpan<byte> b) => !(b[0] is 0 or 10 or 127 or >= 224
        || b[0] == 100 && b[1] is >= 64 and <= 127
        || b[0] == 169 && b[1] == 254
        || b[0] == 172 && b[1] is >= 16 and <= 31
        || b[0] == 192 && b[1] == 168
        || b[0] == 192 && b[1] == 0 && b[2] is 0 or 2
        || b[0] == 198 && b[1] is 18 or 19
        || b[0] == 198 && b[1] == 51 && b[2] == 100
        || b[0] == 203 && b[1] == 0 && b[2] == 113);

    // A connection, made here so a redirect's reaches only a public address, the one it has at this moment: a name that resolves
    // inward, at once or by changing its answer since it was last asked, is refused with no packet sent to it. The owner's own
    // link connects as it would anyway. A proxy, if the install has one, is what connects: a private one refuses redirects.
    public static async ValueTask<Stream> Connect(string host, int port, bool redirected, Func<string, CancellationToken, Task<IPAddress[]>> addressesOf, CancellationToken ct)
    {
        var name = host.Trim('[', ']');
        var addresses = IPAddress.TryParse(name, out var literal) ? [literal] : await addressesOf(name, ct);
        if (redirected) addresses = [.. addresses.Where(Public)];
        if (addresses.Length == 0) throw redirected ? new CalendarAddressRefused() : new SocketException((int)SocketError.HostNotFound);
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }
}
