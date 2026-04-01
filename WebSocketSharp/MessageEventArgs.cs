using System;

namespace WebSocketSharp;

public class MessageEventArgs : EventArgs
{
    private readonly string _data;

    private readonly Opcode _opcode;

    private readonly byte[] _rawData;

    internal Opcode Opcode => _opcode;

    public string Data => _data;

    public bool IsBinary => _opcode == Opcode.Binary;

    public bool IsPing => _opcode == Opcode.Ping;

    public bool IsText => _opcode == Opcode.Text;

    public byte[] RawData => _rawData;

    internal MessageEventArgs(string data)
    {
        _data = data;
        _rawData = null;
        _opcode = Opcode.Text;
    }

    internal MessageEventArgs(Opcode opcode, byte[] rawData)
    {
        /*if ((ulong)rawData.LongLength > PayloadData.MaxLength)
		{
			throw new WebSocketException(CloseStatusCode.TooBig);
		}*/
        // Implement similar safeguards
        _opcode = opcode;
        _rawData = rawData;
    }
}
