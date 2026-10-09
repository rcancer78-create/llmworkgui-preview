using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LLMWorkGUI.App.Services;

/// <summary>
/// Why a local file cannot supply the bytes of a stage artifact.
///
/// The screen turns one of these into a named refusal and puts nothing else on it: the path, the file
/// name, the file bytes and the message of whatever IO exception occurred never leave this type, so they
/// cannot reach a <c>Blocker</c>, a status line, a notice, a log or persisted evidence.
/// </summary>
public enum StageArtifactFileFault
{
    /// <summary>The file is readable and within the bound, and a stream over it was handed back.</summary>
    None,

    /// <summary>Nothing exists at the given local path, or the path names nothing that can be opened.</summary>
    Missing,

    /// <summary>The given local path names a directory, which is not an artifact.</summary>
    Directory,

    /// <summary>The file exists but its bytes cannot be read: a sharing lock, a permission or a fault.</summary>
    Unreadable,

    /// <summary>The file is larger than the bound the screen streams artifacts under.</summary>
    Oversized
}

/// <summary>
/// A file turned out to be larger than the bound while its bytes were already being read, which is the
/// only moment a file can grow past a length that was probed earlier.
///
/// It is an <see cref="IOException"/> because that is what it is, so a caller classifying IO failures has
/// to catch this one first. It deliberately carries neither the path nor the length: the message is the
/// only part of it that can ever reach a human, and a size limit is not user data.
/// </summary>
public sealed class StageArtifactFileTooLargeException : IOException
{
    public StageArtifactFileTooLargeException()
        : base("The artifact file is larger than the bounded artifact size limit of the screen.")
    {
    }
}

/// <summary>
/// Opens a local artifact file as a read-only, forward-only and length-bounded stream for the composed
/// run service.
///
/// The bound is enforced twice, because a single probe is not a guarantee: the length is checked before
/// the file is opened and again from the opened handle, and the returned stream throws
/// <see cref="StageArtifactFileTooLargeException"/> the moment the bytes actually read pass the bound. The
/// bytes themselves are never accumulated here - the run service streams them straight into the
/// application's content-addressed store - so a file at the limit costs the store's buffer and not the
/// screen's memory.
/// </summary>
public static class StageArtifactFileInput
{
    private const int BufferSize = 81920;

    /// <summary>
    /// Opens <paramref name="path"/> for reading, or reports the one reason it cannot be opened.
    /// </summary>
    /// <param name="content">
    /// The bounded read window, or null when <paramref name="fault"/> is anything but
    /// <see cref="StageArtifactFileFault.None"/>. The caller owns it and must dispose it.
    /// </param>
    public static StageArtifactFileFault TryOpen(string? path, long maxBytes, out Stream? content)
    {
        content = null;

        if (string.IsNullOrWhiteSpace(path))
        {
            return StageArtifactFileFault.Missing;
        }

        try
        {
            if (Directory.Exists(path))
            {
                return StageArtifactFileFault.Directory;
            }

            if (!File.Exists(path))
            {
                return StageArtifactFileFault.Missing;
            }

            if (new FileInfo(path).Length > maxBytes)
            {
                return StageArtifactFileFault.Oversized;
            }
        }
        catch (Exception exception) when (IsIoFault(exception))
        {
            return StageArtifactFileFault.Unreadable;
        }

        FileStream file;

        try
        {
            file = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (FileNotFoundException)
        {
            return StageArtifactFileFault.Missing;
        }
        catch (DirectoryNotFoundException)
        {
            return StageArtifactFileFault.Missing;
        }
        catch (Exception exception) when (IsIoFault(exception))
        {
            // A sharing violation, a denied handle and a device that cannot be read as a file are all
            // "unreadable" here: the screen names the class and drops the exception that said more.
            return StageArtifactFileFault.Unreadable;
        }

        try
        {
            if (file.Length > maxBytes)
            {
                file.Dispose();
                return StageArtifactFileFault.Oversized;
            }
        }
        catch (Exception exception) when (IsIoFault(exception))
        {
            file.Dispose();
            return StageArtifactFileFault.Unreadable;
        }

        content = new BoundedArtifactStream(file, maxBytes);
        return StageArtifactFileFault.None;
    }

    private static bool IsIoFault(Exception exception) =>
        exception is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException;

    /// <summary>
    /// A forward-only window that counts the bytes handed out and refuses to hand out more than the bound.
    ///
    /// Only the read surface is implemented on purpose: the run service streams the artifact into the blob
    /// store and never seeks, never writes and never asks for the full length, so offering the rest of the
    /// <see cref="Stream"/> contract would be a capability this window does not actually have.
    /// </summary>
    private sealed class BoundedArtifactStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _read;

        public BoundedArtifactStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException("The artifact window is forward-only.");

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException("The artifact window is forward-only.");
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);
            Count(read);
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = _inner.Read(buffer);
            Count(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Count(read);
            return read;
        }

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var pending = _inner.ReadAsync(buffer, offset, count, cancellationToken);

            return pending.IsCompletedSuccessfully
                ? Task.FromResult(Count(pending.Result))
                : AwaitedAsync(pending);

            async Task<int> AwaitedAsync(Task<int> awaitable)
            {
                return Count(await awaitable.ConfigureAwait(false));
            }
        }

        public override int ReadByte()
        {
            var value = _inner.ReadByte();

            if (value >= 0)
            {
                Count(1);
            }

            return value;
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException("The artifact window is forward-only.");

        public override void SetLength(long value) =>
            throw new NotSupportedException("The artifact window is read-only.");

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException("The artifact window is read-only.");

        public override void Flush()
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
            await base.DisposeAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// Counts the bytes already handed out and throws the moment the file is over the bound. Throwing
        /// rather than truncating is the point: a short read would be committed as if it were the whole
        /// file, and the run would then hold an artifact that never matched the operator's bytes.
        /// </summary>
        private int Count(int read)
        {
            _read += read;

            if (_read > _maxBytes)
            {
                throw new StageArtifactFileTooLargeException();
            }

            return read;
        }
    }
}
