using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using static PdfReader.Core.Engine.Pdfium.PdfiumNative;

namespace PdfReader.Core.Engine.Pdfium;

/// <summary>
/// Feeds a PDF file to PDFium (FPDF_LoadCustomDocument). PDFium reads lazily for the whole life of the
/// document, so the file stays open; it is opened with delete sharing so that saving can replace it.
/// After an incremental save the source switches to the new file, whose first <see cref="Length"/>
/// bytes are identical to the original (see <see cref="PdfiumDocument.Save"/>).
/// </summary>
internal sealed unsafe class PdfFileSource : IDisposable
{
    private SafeFileHandle? _handle;
    private GCHandle _self;
    private FPDF_FILEACCESS* _access;

    private PdfFileSource(SafeFileHandle handle, long length)
    {
        _handle = handle;
        Length = length;
        _self = GCHandle.Alloc(this);
        _access = (FPDF_FILEACCESS*)NativeMemory.AllocZeroed((nuint)sizeof(FPDF_FILEACCESS));
        _access->FileLen = (uint)length;
        _access->GetBlock = &GetBlock;
        _access->Param = (void*)GCHandle.ToIntPtr(_self);
    }

    /// <summary>Size of the file as loaded; PDFium never reads beyond it.</summary>
    public long Length { get; }

    public FPDF_FILEACCESS* Access => _access;

    /// <summary>PDFium's custom access takes a 32-bit length on Windows.</summary>
    public static bool Supports(long length) => length <= uint.MaxValue;

    public static PdfFileSource Open(string path)
    {
        var handle = OpenHandle(path);
        try
        {
            return new PdfFileSource(handle, RandomAccess.GetLength(handle));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public static SafeFileHandle OpenHandle(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Reads from another file from now on (null: none, reads fail). Its first <see cref="Length"/> bytes must
    /// be identical.
    /// </summary>
    public void SwitchTo(SafeFileHandle? handle)
    {
        var old = _handle;
        _handle = handle;
        old?.Dispose();
    }

    /// <summary>True when <paramref name="other"/> starts with exactly the bytes PDFium loaded.</summary>
    public bool IsPrefixOf(SafeFileHandle other)
    {
        if (_handle is null) return false;
        if (RandomAccess.GetLength(other) < Length) return false;
        const int Chunk = 1 << 20;
        var a = new byte[Chunk];
        var b = new byte[Chunk];
        for (long position = 0; position < Length; position += Chunk)
        {
            int size = (int)Math.Min(Chunk, Length - position);
            if (!ReadExactly(_handle, position, a.AsSpan(0, size)) || !ReadExactly(other, position, b.AsSpan(0, size)))
                return false;
            if (!a.AsSpan(0, size).SequenceEqual(b.AsSpan(0, size))) return false;
        }
        return true;
    }

    private static bool ReadExactly(SafeFileHandle handle, long position, Span<byte> buffer)
    {
        while (buffer.Length > 0)
        {
            int read = RandomAccess.Read(handle, buffer, position);
            if (read <= 0) return false;
            buffer = buffer[read..];
            position += read;
        }
        return true;
    }

    [UnmanagedCallersOnly]
    private static int GetBlock(void* param, uint position, byte* buffer, uint size)
    {
        try
        {
            var source = (PdfFileSource)GCHandle.FromIntPtr((nint)param).Target!;
            return source._handle is { } handle && ReadExactly(handle, position, new Span<byte>(buffer, (int)size)) ? 1 : 0;
        }
        catch
        {
            return 0; // PDFium treats it as a read error; exceptions must not cross into native code
        }
    }

    public void Dispose()
    {
        if (_access is null) return;
        _handle?.Dispose();
        NativeMemory.Free(_access);
        _access = null;
        _self.Free();
    }
}
