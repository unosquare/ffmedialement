﻿namespace Unosquare.FFME.Decoding
{
    using FFmpeg.AutoGen;
    using Shared;
    using System;
    using System.Collections.Generic;

    /// <summary>
    /// A data structure containing a quque of packets to process.
    /// This class is thread safe and disposable.
    /// Enqueued, unmanaged packets are disposed automatically by this queue.
    /// Dequeued packets are the responsibility of the calling code.
    /// </summary>
    internal sealed unsafe class PacketQueue : IDisposable
    {
        #region Private Declarations

        private readonly List<MediaPacket> Packets = new List<MediaPacket>(2048);
        private readonly object SyncLock = new object();
        private long m_BufferLength;
        private long m_Duration;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the packet count.
        /// </summary>
        public int Count
        {
            get { lock (SyncLock) return Packets.Count; }
        }

        /// <summary>
        /// Gets the sum of all the packet sizes contained
        /// by this queue.
        /// </summary>
        public long BufferLength
        {
            get { lock (SyncLock) return m_BufferLength; }
        }

        /// <summary>
        /// Gets or sets the <see cref="AVPacket"/> at the specified index.
        /// </summary>
        /// <value>
        /// The <see cref="AVPacket"/>.
        /// </value>
        /// <param name="index">The index.</param>
        /// <returns>The packet reference</returns>
        private MediaPacket this[int index]
        {
            get { lock (SyncLock) return Packets[index]; }
            set { lock (SyncLock) Packets[index] = value; }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Gets the duration in stream time base units.
        /// </summary>
        /// <param name="timeBase">The time base.</param>
        /// <returns>The total duration</returns>
        public TimeSpan GetDuration(AVRational timeBase)
        {
            lock (SyncLock) { return m_Duration.ToTimeSpan(timeBase); }
        }

        /// <summary>
        /// Peeks the next available packet in the queue without removing it.
        /// If no packets are available, null is returned.
        /// </summary>
        /// <returns>The packet</returns>
        public MediaPacket Peek()
        {
            lock (SyncLock)
            {
                if (Packets.Count <= 0) return null;
                return Packets[0];
            }
        }

        /// <summary>
        /// Pushes the specified packet into the queue.
        /// In other words, enqueues the packet.
        /// </summary>
        /// <param name="packet">The packet.</param>
        public void Push(MediaPacket packet)
        {
            // avoid pushing null packets
            if (packet == null) return;

            lock (SyncLock)
            {
                Packets.Add(packet);
                m_BufferLength += packet.Size < 0 ? default : packet.Size;
                m_Duration += packet.Duration < 0 ? default : packet.Duration;
            }
        }

        /// <summary>
        /// Dequeues a packet from this queue.
        /// </summary>
        /// <returns>The dequeued packet</returns>
        public MediaPacket Dequeue()
        {
            lock (SyncLock)
            {
                if (Packets.Count <= 0) return null;
                var result = Packets[0];
                Packets.RemoveAt(0);

                var packet = result;
                m_BufferLength -= packet.Size < 0 ? default : packet.Size;
                m_Duration -= packet.Duration < 0 ? default : packet.Duration;
                return packet;
            }
        }

        /// <summary>
        /// Clears and frees all the unmanaged packets from this queue.
        /// </summary>
        public void Clear()
        {
            lock (SyncLock)
            {
                while (Packets.Count > 0)
                {
                    var packet = Dequeue();
                    packet.Dispose();
                }

                m_BufferLength = 0;
                m_Duration = 0;
            }
        }

        #endregion

        #region IDisposable Support

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose() => Dispose(true);

        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="alsoManaged"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        private void Dispose(bool alsoManaged)
        {
            if (alsoManaged == false)
                return;

            lock (SyncLock)
                Clear();
        }

        #endregion
    }
}
