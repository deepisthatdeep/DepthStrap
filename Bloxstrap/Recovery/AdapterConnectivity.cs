using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DepthStrap.Recovery;

internal static class AdapterConnectivity
{
    internal static (SocketOptionLevel Level, int Value) OutgoingInterfaceOption(AddressFamily family, int index)
    {
        if (index <= 0) throw new ArgumentException("A concrete outgoing interface is required.");
        return family switch
        {
            AddressFamily.InterNetwork when index <= 0x00ffffff => (SocketOptionLevel.IP, IPAddress.HostToNetworkOrder(index)),
            AddressFamily.InterNetworkV6 => (SocketOptionLevel.IPv6, index),
            _ => throw new ArgumentException("Unsupported interface family or index.")
        };
    }
    internal static void PinSocket(Socket socket, int index)
    {
        var option = OutgoingInterfaceOption(socket.AddressFamily, index);
        // Windows SDK: IP_UNICAST_IF and IPV6_UNICAST_IF are both option 31.
        // IPv4 sets network order; both families return the index in host order.
        const SocketOptionName unicastInterface = (SocketOptionName)31;
        socket.SetSocketOption(option.Level, unicastInterface, option.Value);
        if ((int)socket.GetSocketOption(option.Level, unicastInterface)! != index)
            throw new IOException("The outgoing probe interface could not be verified.");
    }
    internal static bool IsUsableSource(IPAddress source)
    {
        if (IPAddress.IsLoopback(source) || source.Equals(IPAddress.Any) || source.Equals(IPAddress.IPv6Any)) return false;
        byte[] bytes = source.GetAddressBytes();
        return source.AddressFamily switch
        {
            AddressFamily.InterNetwork => bytes[0] is > 0 and < 224 && !(bytes[0] == 169 && bytes[1] == 254),
            AddressFamily.InterNetworkV6 => !source.IsIPv6LinkLocal && !source.IsIPv6Multicast && !source.IsIPv4MappedToIPv6,
            _ => false
        };
    }
    internal static async Task<bool> ProbeAsync(IPAddress source, int interfaceIndex, CancellationToken token)
    {
        if (!IsUsableSource(source) || interfaceIndex <= 0) return false;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token); budget.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var destinations = await Dns.GetHostAddressesAsync("google.com", budget.Token);
            var destination = destinations.FirstOrDefault(address => address.AddressFamily == source.AddressFamily);
            // A source-bound ICMP reply alone does not establish which interface
            // carried it on a weak-host configuration. HTTPS must also pass.
            if (source.AddressFamily == AddressFamily.InterNetwork && destination is not null)
                await Task.Run(() => Ping(source, destination), budget.Token);
            using var handler = new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectCallback = async (context, cancellation) =>
                {
                    var socket = new Socket(source.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        PinSocket(socket, interfaceIndex);
                        socket.Bind(new IPEndPoint(source, 0));
                        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation);
                        var candidates = addresses.Where(address => address.AddressFamily == source.AddressFamily).ToArray();
                        if (candidates.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
                        await socket.ConnectAsync(candidates, context.DnsEndPoint.Port, cancellation);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch { socket.Dispose(); throw; }
                }
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(7) };
            using var response = await client.GetAsync("https://www.google.com/generate_204", HttpCompletionOption.ResponseHeadersRead, budget.Token);
            return response.StatusCode == HttpStatusCode.NoContent;
        }
        catch (Exception ex) when (ex is SocketException or HttpRequestException or OperationCanceledException or InvalidOperationException or IOException or ArgumentException) { return false; }
    }
    private static bool Ping(IPAddress source, IPAddress destination)
    {
        using var handle = IcmpCreateFile();
        if (handle.IsInvalid) return false;
        byte[] data = Encoding.ASCII.GetBytes("DepthStrap connectivity");
        byte[] reply = new byte[1024];
        uint count = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, BitConverter.ToUInt32(source.GetAddressBytes()),
            BitConverter.ToUInt32(destination.GetAddressBytes()), data, (ushort)data.Length, IntPtr.Zero, reply, (uint)reply.Length, 1200);
        return count > 0 && BitConverter.ToUInt32(reply, 4) == 0;
    }
    private sealed class IcmpHandle : SafeHandleZeroOrMinusOneIsInvalid
    { private IcmpHandle() : base(true) { } protected override bool ReleaseHandle() => IcmpCloseHandle(handle); }
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern IcmpHandle IcmpCreateFile();
    [DllImport("iphlpapi.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IcmpCloseHandle(IntPtr handle);
    [DllImport("iphlpapi.dll", SetLastError = true)] private static extern uint IcmpSendEcho2Ex(IcmpHandle handle, IntPtr completionEvent, IntPtr callback, IntPtr context,
        uint source, uint destination, byte[] request, ushort length, IntPtr options, [Out] byte[] reply, uint replyLength, uint timeout);
}
