using System;
using System.IO;

namespace netHEmusic.Core.Native;

/// <summary>
/// 只吐出前 N 字节的只读流包装。
/// 用途：本地音频要按 HTTP Range 回一段（206 Partial Content）时，把文件流裁成请求的那一段。
/// </summary>
internal sealed class LimitedStream : Stream
{
    private readonly Stream _inner;
    private long _left;

    public LimitedStream(Stream inner, long length) { _inner = inner; _left = Math.Max(0, length); }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _left;
    public override long Position { get => 0; set { } }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_left <= 0) return 0;
        var n = _inner.Read(buffer, offset, (int)Math.Min(count, _left));
        _left -= n;
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { try { _inner.Dispose(); } catch { } }
        base.Dispose(disposing);
    }
}
