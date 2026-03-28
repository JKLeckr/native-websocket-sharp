using System;

namespace WebSocketSharp;

public class CloseEventArgs : EventArgs
{
	private readonly bool _clean;

    private readonly ushort _code;

    private readonly string _reason;

	public ushort Code => _code;

	public string Reason => _reason;

	public bool WasClean => _clean;

	internal CloseEventArgs(ushort code, string reason, bool clean)
	{
        _code = code;
        _reason = reason;
		_clean = clean;
	}
}
