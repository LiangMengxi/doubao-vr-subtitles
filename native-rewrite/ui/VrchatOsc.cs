using System.Net.Sockets;
using System.Text;

namespace DoubaoVR.Desktop;

internal static class VrchatOsc
{
    private static readonly Socket Udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

    public static async Task SendChatboxAsync(string text, bool direct = true, bool notify = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        string safeText = text.Trim();
        if (safeText.Length > 144) safeText = safeText[..141] + "...";

        byte[] packet = BuildOscMessage("/chatbox/input", safeText, direct, notify);
        try
        {
            await Udp.SendToAsync(packet, SocketFlags.None, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 9000));
        }
        catch (Exception ex)
        {
            Log.Write($"[OSC] Failed to send chatbox: {ex.Message}");
        }
    }

    public static async Task SendTypingAsync(bool isTyping)
    {
        byte[] packet = BuildOscTyping("/chatbox/typing", isTyping);
        try
        {
            await Udp.SendToAsync(packet, SocketFlags.None, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 9000));
        }
        catch (Exception ex)
        {
            Log.Write($"[OSC] Failed to send typing: {ex.Message}");
        }
    }

    internal static byte[] BuildOscMessage(string address, string text, bool direct, bool notify)
    {
        using var stream = new MemoryStream();
        void WriteString(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0);
            while (stream.Position % 4 != 0) stream.WriteByte(0);
        }

        WriteString(address);
        // Type tags: ,sTT or ,sTF or ,sFT or ,sFF
        char b1 = direct ? 'T' : 'F';
        char b2 = notify ? 'T' : 'F';
        WriteString($",s{b1}{b2}");
        WriteString(text);
        return stream.ToArray();
    }

    internal static byte[] BuildOscTyping(string address, bool isTyping)
    {
        using var stream = new MemoryStream();
        void WriteString(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            stream.Write(bytes, 0, bytes.Length);
            stream.WriteByte(0);
            while (stream.Position % 4 != 0) stream.WriteByte(0);
        }

        WriteString(address);
        WriteString(isTyping ? ",T" : ",F");
        return stream.ToArray();
    }
}
