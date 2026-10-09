using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DepthStrap.Recovery;

internal static class SocketPinSmoke
{
    internal static void Run()
    {
        var loopback = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(item => item.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            ?? throw new IOException("Windows loopback interface is unavailable.");
        var properties = loopback.GetIPProperties();
        int checks = 0;
        foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
        {
            bool ipv4 = family == AddressFamily.InterNetwork;
            if (ipv4 ? !Socket.OSSupportsIPv4 : !Socket.OSSupportsIPv6) continue;
            if (!loopback.Supports(ipv4 ? NetworkInterfaceComponent.IPv4 : NetworkInterfaceComponent.IPv6)) continue;
            int index = ipv4 ? properties.GetIPv4Properties()!.Index : properties.GetIPv6Properties()!.Index;
            using var socket = new Socket(family, SocketType.Stream, ProtocolType.Tcp);
            AdapterConnectivity.PinSocket(socket, index);
            socket.Bind(new IPEndPoint(ipv4 ? IPAddress.Loopback : IPAddress.IPv6Loopback, 0));
            checks++;
        }
        if (checks == 0) throw new IOException("No loopback address family was available for socket verification.");
        Console.WriteLine($"PASS: {checks} Windows socket-interface configuration checks. Loopback sockets only; no connections or packets sent.");
    }
}
