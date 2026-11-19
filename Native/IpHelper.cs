using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetWatch.Native;

public enum Proto { TCP, UDP }

/// <summary>OS の接続テーブルから取得した 1 行分。</summary>
public readonly record struct SocketEntry(
    Proto Protocol,
    IPAddress LocalAddress,
    int LocalPort,
    IPAddress? RemoteAddress,
    int RemotePort,
    string State,
    int Pid);

/// <summary>
/// iphlpapi.dll の GetExtendedTcpTable / GetExtendedUdpTable を使って
/// PID 付きの TCP/UDP 接続一覧（IPv4 / IPv6）を取得する。管理者権限は不要。
/// </summary>
public static class IpHelper
{
    private const int AF_INET = 2;
    private const int AF_INET6 = 23;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;
    private const int UDP_TABLE_OWNER_PID = 1;
    private const uint ERROR_INSUFFICIENT_BUFFER = 122;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTable, ref int size, bool sort, int af, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedUdpTable(IntPtr pTable, ref int size, bool sort, int af, int tableClass, uint reserved);

    private static readonly string[] TcpStates =
    {
        "?", "CLOSED", "LISTEN", "SYN_SENT", "SYN_RCVD", "ESTABLISHED", "FIN_WAIT1",
        "FIN_WAIT2", "CLOSE_WAIT", "CLOSING", "LAST_ACK", "TIME_WAIT", "DELETE_TCB",
    };

    public static List<SocketEntry> GetAll()
    {
        var list = new List<SocketEntry>(512);
        ReadTable(true, AF_INET, list);
        ReadTable(true, AF_INET6, list);
        ReadTable(false, AF_INET, list);
        ReadTable(false, AF_INET6, list);
        return list;
    }

    private static void ReadTable(bool tcp, int af, List<SocketEntry> list)
    {
        int size = 0;
        uint ret = Call(tcp, IntPtr.Zero, ref size, af);
        if (ret != ERROR_INSUFFICIENT_BUFFER && ret != 0) return;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            IntPtr buf = Marshal.AllocHGlobal(size);
            try
            {
                ret = Call(tcp, buf, ref size, af);
                if (ret == ERROR_INSUFFICIENT_BUFFER) continue; // 取得中にテーブルが増えた
                if (ret != 0) return;
                Parse(tcp, af, buf, list);
                return;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
    }

    private static uint Call(bool tcp, IntPtr buf, ref int size, int af) =>
        tcp ? GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0)
            : GetExtendedUdpTable(buf, ref size, false, af, UDP_TABLE_OWNER_PID, 0);

    private static void Parse(bool tcp, int af, IntPtr buf, List<SocketEntry> list)
    {
        int count = Marshal.ReadInt32(buf);
        IntPtr row = buf + 4;

        // 行レイアウト（すべて 4 バイト単位）
        //  MIB_TCPROW_OWNER_PID   : state, localAddr, localPort, remoteAddr, remotePort, pid      = 24
        //  MIB_TCP6ROW_OWNER_PID  : localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid = 56
        //  MIB_UDPROW_OWNER_PID   : localAddr, localPort, pid                                     = 12
        //  MIB_UDP6ROW_OWNER_PID  : localAddr[16], scope, localPort, pid                          = 28
        for (int i = 0; i < count; i++)
        {
            if (tcp && af == AF_INET)
            {
                int state = Marshal.ReadInt32(row, 0);
                var la = new IPAddress((uint)Marshal.ReadInt32(row, 4));
                int lp = Port(Marshal.ReadInt32(row, 8));
                var ra = new IPAddress((uint)Marshal.ReadInt32(row, 12));
                int rp = Port(Marshal.ReadInt32(row, 16));
                int pid = Marshal.ReadInt32(row, 20);
                list.Add(new SocketEntry(Proto.TCP, la, lp, ra, rp, StateName(state), pid));
                row += 24;
            }
            else if (tcp)
            {
                var la = ReadV6(row, 0);
                int lp = Port(Marshal.ReadInt32(row, 20));
                var ra = ReadV6(row, 24);
                int rp = Port(Marshal.ReadInt32(row, 44));
                int state = Marshal.ReadInt32(row, 48);
                int pid = Marshal.ReadInt32(row, 52);
                list.Add(new SocketEntry(Proto.TCP, la, lp, ra, rp, StateName(state), pid));
                row += 56;
            }
            else if (af == AF_INET)
            {
                var la = new IPAddress((uint)Marshal.ReadInt32(row, 0));
                int lp = Port(Marshal.ReadInt32(row, 4));
                int pid = Marshal.ReadInt32(row, 8);
                list.Add(new SocketEntry(Proto.UDP, la, lp, null, 0, "", pid));
                row += 12;
            }
            else
            {
                var la = ReadV6(row, 0);
                int lp = Port(Marshal.ReadInt32(row, 20));
                int pid = Marshal.ReadInt32(row, 24);
                list.Add(new SocketEntry(Proto.UDP, la, lp, null, 0, "", pid));
                row += 28;
            }
        }
    }

    private static IPAddress ReadV6(IntPtr p, int offset)
    {
        var bytes = new byte[16];
        Marshal.Copy(p + offset, bytes, 0, 16);
        return new IPAddress(bytes);
    }

    /// <summary>ポートはネットワークバイトオーダーで下位 16bit に入っている。</summary>
    private static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private static string StateName(int s) => s >= 0 && s < TcpStates.Length ? TcpStates[s] : s.ToString();

    /// <summary>IPv4-mapped IPv6 を IPv4 に揃え、比較用の文字列にする。</summary>
    public static string Normalize(IPAddress? a)
    {
        if (a is null) return "";
        if (a.AddressFamily == AddressFamily.InterNetworkV6 && a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily == AddressFamily.InterNetworkV6) a.ScopeId = 0;
        return a.ToString();
    }

    public static bool IsUnspecified(IPAddress? a) =>
        a is null || a.Equals(IPAddress.Any) || a.Equals(IPAddress.IPv6Any);

    public static bool IsUnspecifiedText(string a) => a is "0.0.0.0" or "::" or "";

    public static bool IsLoopback(IPAddress? a) => a is not null && IPAddress.IsLoopback(a);
}
