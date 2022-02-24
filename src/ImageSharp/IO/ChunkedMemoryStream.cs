// Copyright (c) Six Labors.
// Licensed under the Apache License, Version 2.0.

using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using SixLabors.ImageSharp.Memory;

namespace SixLabors.ImageSharp.IO
{
    /// <summary>
    /// Provides an in-memory stream composed of non-contiguous chunks that doesn't need to be resized.
    /// Chunks are allocated by the <see cref="MemoryAllocator"/> assigned via the constructor
    /// and is designed to take advantage of buffer pooling when available.
    /// </summary>
    internal sealed class ChunkedMemoryStream : Stream
    {
        /// <summary>
        /// The default length in bytes of each buffer chunk when allocating large buffers.
        /// </summary>
        public const int DefaultLargeChunkSize = 1024 * 1024 * 4; // 4 Mb

        /// <summary>
        /// The threshold at which to switch to using the large buffer.
        /// </summary>
        public const int DefaultLargeChunkThreshold = DefaultLargeChunkSize / 4; // 1 Mb

        /// <summary>
        /// The default length in bytes of each buffer chunk when allocating small buffers.
        /// </summary>
        public const int DefaultSmallChunkSize = DefaultLargeChunkSize / 32; // 128 Kb

        // The memory allocator.
        private readonly MemoryAllocator allocator;

        // Data
        private MemoryChunk memoryChunk;

        // The length, in bytes, of each large buffer chunk
        private readonly int largeChunkSize;

        // The length, in bytes of the total allocation threshold that triggers switching from
        // small to large buffer chunks.
        private readonly int largeChunkThreshold;

        // The current length, in bytes, of each buffer chunk
        private int chunkSize;

        // The total allocation length, in bytes
        private int totalAllocation;

        // Has the stream been disposed.
        private bool isDisposed;

        // Current chunk to write to
        private MemoryChunk writeChunk;

        // Offset into chunk to write to
        private int writeOffset;

        // Current chunk to read from
        private MemoryChunk readChunk;

        // Offset into chunk to read from
        private int readOffset;

        /// <summary>
        /// Initializes a new instance of the <see cref="ChunkedMemoryStream"/> class.
        /// </summary>
        public ChunkedMemoryStream(MemoryAllocator allocator)
        {
            Guard.NotNull(allocator, nameof(allocator));

            // Tweak our buffer sizes to take the minimum of the provided buffer sizes
            // or values scaled from the allocator buffer capacity which provides us with the largest
            // available contiguous buffer size.
            this.largeChunkSize = Math.Min(DefaultLargeChunkSize, allocator.GetBufferCapacityInBytes());
            this.largeChunkThreshold = Math.Min(DefaultLargeChunkThreshold, this.largeChunkSize / 4);
            this.chunkSize = Math.Min(DefaultSmallChunkSize, this.largeChunkSize / 32);
            this.allocator = allocator;
        }

        /// <inheritdoc/>
        public override bool CanRead => !this.isDisposed;

        /// <inheritdoc/>
        public override bool CanSeek => !this.isDisposed;

        /// <inheritdoc/>
        public override bool CanWrite => !this.isDisposed;

        /// <inheritdoc/>
        public override long Length
        {
            get
            {
                this.EnsureNotDisposed();

                int length = 0;
                MemoryChunk chunk = this.memoryChunk;
                while (chunk != null)
                {
                    MemoryChunk next = chunk.Next;
                    if (next != null)
                    {
                        length += chunk.Length;
                    }
                    else
                    {
                        length += this.writeOffset;
                    }

                    chunk = next;
                }

                return length;
            }
        }

        /// <inheritdoc/>
        public override long Position
        {
            get
            {
                this.EnsureNotDisposed();

                if (this.readChunk is null)
                {
                    return 0;
                }

                int pos = 0;
                MemoryChunk chunk = this.memoryChunk;
                while (chunk != this.readChunk)
                {
                    pos += chunk.Length;
                    chunk = chunk.Next;
                }

                pos += this.readOffset;

                return pos;
            }

            set
            {
                this.EnsureNotDisposed();

                if (value < 0)
                {
                    ThrowArgumentOutOfRange(nameof(value));
                }

                // Back up current position in case new position is out of range
                MemoryChunk backupReadChunk = this.readChunk;
                int backupReadOffset = this.readOffset;

                this.readChunk = null;
                this.readOffset = 0;

                int leftUntilAtPos = (int)value;
                MemoryChunk chunk = this.memoryChunk;
                while (chunk != null)
                {
                    if ((leftUntilAtPos < chunk.Length)
                            || ((leftUntilAtPos == chunk.Length)
                            && (chunk.Next is null)))
                    {
                        // The desired position is in this chunk
                        this.readChunk = chunk;
                        this.readOffset = leftUntilAtPos;
                        break;
                    }

                    leftUntilAtPos -= chunk.Length;
                    chunk = chunk.Next;
                }

                if (this.readChunk is null)
                {
                    // Position is out of range
                    this.readChunk = backupReadChunk;
                    this.readOffset = backupReadOffset;
                }
            }
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override long Seek(long offset, SeekOrigin origin)
        {
            this.EnsureNotDisposed();

            switch (origin)
            {
                case SeekOrigin.Begin:
                    this.Position = offset;
                    break;

                case SeekOrigin.Current:
                    this.Position += offset;
                    break;

                case SeekOrigin.End:
                    this.Position = this.Length + offset;
                    break;
                default:
                    ThrowInvalidSeek();
                    break;
            }

            return this.Position;
        }

        /// <inheritdoc/>
        public override void SetLength(long value)
            => throw new NotSupportedException();

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (this.isDisposed)
            {
                return;
            }

            try
            {
                this.isDisposed = true;
                if (disposing)
                {
                    this.ReleaseMemoryChunks(this.memoryChunk);
                }

                this.memoryChunk = null;
                this.writeChunk = null;
                this.readChunk = null;
                this.totalAllocation = 0;
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        /// <inheritdoc/>
        public override void Flush()
        {
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int Read(byte[] buffer, int offset, int count)
        {
            Guard.NotNull(buffer, nameof(buffer));
            Guard.MustBeGreaterThanOrEqualTo(offset, 0, nameof(offset));
            Guard.MustBeGreaterThanOrEqualTo(count, 0, nameof(count));

            const string bufferMessage = "Offset subtracted from the buffer length is less than count.";
            Guard.IsFalse(buffer.Length - offset < count, nameof(buffer), bufferMessage);

            return this.ReadImpl(buffer.AsSpan(offset, count));
        }

#if SUPPORTS_SPAN_STREAM
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int Read(Span<byte> buffer) => this.ReadImpl(buffer);
#endif

        private int ReadImpl(Span<byte> buffer)
        {
            this.EnsureNotDisposed();

            if (this.readChunk is null)
            {
                if (this.memoryChunk is null)
                {
                    return 0;
                }

                this.readChunk = this.memoryChunk;
                this.readOffset = 0;
            }

            IMemoryOwner<byte> chunkBuffer = this.readChunk.Buffer;
            int chunkSize = this.readChunk.Length;
            if (this.readChunk.Next is null)
            {
                chunkSize = this.writeOffset;
            }

            int bytesRead = 0;
            int offset = 0;
            int count = buffer.Length;
            while (count > 0)
            {
                if (this.readOffset == chunkSize)
                {
                    // Exit if no more chunks are currently available
                    if (this.readChunk.Next is null)
                    {
                        break;
                    }

                    this.readChunk = this.readChunk.Next;
                    this.readOffset = 0;
                    chunkBuffer = this.readChunk.Buffer;
                    chunkSize = this.readChunk.Length;
                    if (this.readChunk.Next is null)
                    {
                        chunkSize = this.writeOffset;
                    }
                }

                int readCount = Math.Min(count, chunkSize - this.readOffset);
                chunkBuffer.Slice(this.readOffset, readCount).CopyTo(buffer.Slice(offset));
                offset += readCount;
                count -= readCount;
                this.readOffset += readCount;
                bytesRead += readCount;
            }

            return bytesRead;
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override int ReadByte()
        {
            this.EnsureNotDisposed();

            if (this.readChunk is null)
            {
                if (this.memoryChunk is null)
                {
                    return 0;
                }

                this.readChunk = this.memoryChunk;
                this.readOffset = 0;
            }

            IMemoryOwner<byte> chunkBuffer = this.readChunk.Buffer;
            int chunkSize = this.readChunk.Length;
            if (this.readChunk.Next is null)
            {
                chunkSize = this.writeOffset;
            }

            if (this.readOffset == chunkSize)
            {
                // Exit if no more chunks are currently available
                if (this.readChunk.Next is null)
                {
                    return -1;
                }

                this.readChunk = this.readChunk.Next;
                this.readOffset = 0;
                chunkBuffer = this.readChunk.Buffer;
            }

            return chunkBuffer.GetSpan()[this.readOffset++];
        }

        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Write(byte[] buffer, int offset, int count)
        {
            Guard.NotNull(buffer, nameof(buffer));
            Guard.MustBeGreaterThanOrEqualTo(offset, 0, nameof(offset));
            Guard.MustBeGreaterThanOrEqualTo(count, 0, nameof(count));

            const string bufferMessage = "Offset subtracted from the buffer length is less than count.";
            Guard.IsFalse(buffer.Length - offset < count, nameof(buffer), bufferMessage);

            this.WriteImpl(buffer.AsSpan(offset, count));
        }

#if SUPPORTS_SPAN_STREAM
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override void Write(ReadOnlySpan<byte> buffer) => this.WriteImpl(buffer);
#endif

        private void WriteImpl(ReadOnlySpan<byte> buffer)
        {
            this.EnsureNotDisposed();

            if (this.memoryChunk is null)
            {
                this.memoryChunk = this.AllocateMemoryChunk();
                this.writeChunk = this.memoryChunk;
                this.writeOffset = 0;
            }

            Span<byte> chunkBuffer = this.writeChunk.Buffer.GetSpan();
            int chunkSize = this.writeChunk.Length;
            int count = buffer.Length;
            int offset = 0;
            while (count > 0)
            {
                if (this.writeOffset == chunkSize)
                {
                    // Allocate a new chunk if the current one is full
                    this.writeChunk.Next = this.AllocateMemoryChunk();
                    this.writeChunk = this.writeChunk.Next;
                    this.writeOffset = 0;
                    chunkBuffer = this.writeChunk.Buffer.GetSpan();
                    chunkSize = this.writeChunk.Length;
                }

                int copyCount = Math.Min(count, chunkSize - this.writeOffset);
                buffer.Slice(offset, copyCount).CopyTo(chunkBuffer.Slice(this.writeOffset));

                offset += copyCount;
                count -= copyCount;
                this.writeOffset += copyCount;
            }
        }

        /// <inheritdoc/>
        public override void WriteByte(byte value)
        {
            this.EnsureNotDisposed();

            if (this.memoryChunk is null)
            {
                this.memoryChunk = this.AllocateMemoryChunk();
                this.writeChunk = this.memoryChunk;
                this.writeOffset = 0;
            }

            IMemoryOwner<byte> chunkBuffer = this.writeChunk.Buffer;
            int chunkSize = this.writeChunk.Length;

            if (this.writeOffset == chunkSize)
            {
                // Allocate a new chunk if the current one is full
                this.writeChunk.Next = this.AllocateMemoryChunk();
                this.writeChunk = this.writeChunk.Next;
                this.writeOffset = 0;
                chunkBuffer = this.writeChunk.Buffer;
            }

            chunkBuffer.GetSpan()[this.writeOffset++] = value;
        }

        /// <summary>
        /// Copy entire buffer into an array.
        /// </summary>
        /// <returns>The <see cref="T:byte[]"/>.</returns>
        public byte[] ToArray()
        {
            int length = (int)this.Length; // This will throw if stream is closed
            byte[] copy = new byte[this.Length];

            MemoryChunk backupReadChunk = this.readChunk;
            int backupReadOffset = this.readOffset;

            this.readChunk = this.memoryChunk;
            this.readOffset = 0;
            this.Read(copy, 0, length);

            this.readChunk = backupReadChunk;
            this.readOffset = backupReadOffset;

            return copy;
        }

        /// <summary>
        /// Write remainder of this stream to another stream.
        /// </summary>
        /// <param name="stream">The stream to write to.</param>
        public void WriteTo(Stream stream)
        {
            this.EnsureNotDisposed();

            Guard.NotNull(stream, nameof(stream));

            if (this.readChunk is null)
            {
                if (this.memoryChunk is null)
                {
                    return;
                }

                this.readChunk = this.memoryChunk;
                this.readOffset = 0;
            }

            IMemoryOwner<byte> chunkBuffer = this.readChunk.Buffer;
            int chunkSize = this.readChunk.Length;
            if (this.readChunk.Next is null)
            {
                chunkSize = this.writeOffset;
            }

            // Following code mirrors Read() logic (readChunk/readOffset should
            // point just past last byte of last chunk when done)
            // loop until end of chunks is found
            while (true)
            {
                if (this.readOffset == chunkSize)
                {
                    // Exit if no more chunks are currently available
                    if (this.readChunk.Next is null)
                    {
                        break;
                    }

                    this.readChunk = this.readChunk.Next;
                    this.readOffset = 0;
                    chunkBuffer = this.readChunk.Buffer;
                    chunkSize = this.readChunk.Length;
                    if (this.readChunk.Next is null)
                    {
                        chunkSize = this.writeOffset;
                    }
                }

                int writeCount = chunkSize - this.readOffset;
                stream.Write(chunkBuffer.GetSpan(), this.readOffset, writeCount);
                this.readOffset = chunkSize;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureNotDisposed()
        {
            if (this.isDisposed)
            {
                ThrowDisposed();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowDisposed() => throw new ObjectDisposedException(null, "The stream is closed.");

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowArgumentOutOfRange(string value) => throw new ArgumentOutOfRangeException(value);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowInvalidSeek() => throw new ArgumentException("Invalid seek origin.");

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private MemoryChunk AllocateMemoryChunk()
        {
            if (this.totalAllocation >= this.largeChunkThreshold)
            {
                this.chunkSize = this.largeChunkSize;
            }

            IMemoryOwner<byte> buffer = this.allocator.Allocate<byte>(this.chunkSize);
            this.totalAllocation += this.chunkSize;

            return new MemoryChunk
            {
                Buffer = buffer,
                Next = null,
                Length = buffer.Length()
            };
        }

        private void ReleaseMemoryChunks(MemoryChunk chunk)
        {
            while (chunk != null)
            {
                chunk.Dispose();
                chunk = chunk.Next;
            }
        }

        private sealed class MemoryChunk : IDisposable
        {
            private bool isDisposed;

            public IMemoryOwner<byte> Buffer { get; set; }

            public MemoryChunk Next { get; set; }

            public int Length { get; set; }

            private void Dispose(bool disposing)
            {
                if (!this.isDisposed)
                {
                    if (disposing)
                    {
                        this.Buffer.Dispose();
                    }

                    this.Buffer = null;
                    this.isDisposed = true;
                }
            }

            public void Dispose()
            {
                this.Dispose(disposing: true);
                GC.SuppressFinalize(this);
            }
        }
    }
}
