﻿namespace Unosquare.FFME
{
    using Primitives;
    using Shared;
    using System;
    using System.Linq;
    using System.Runtime.CompilerServices;
    using Workers;

    public partial class MediaEngine
    {
        /// <summary>
        /// This partial class implements:
        /// 1. Packet reading from the Container
        /// 2. Frame Decoding from packet buffer and Block buffering
        /// 3. Block Rendering from block buffer
        /// </summary>

        #region State Management

        private readonly AtomicBoolean m_IsSyncBuffering = new AtomicBoolean(false);
        private readonly AtomicBoolean m_HasDecodingEnded = new AtomicBoolean(false);

        /// <summary>
        /// Holds the materialized block cache for each media type.
        /// </summary>
        public MediaTypeDictionary<MediaBlockBuffer> Blocks { get; } = new MediaTypeDictionary<MediaBlockBuffer>();

        /// <summary>
        /// Gets the preloaded subtitle blocks.
        /// </summary>
        public MediaBlockBuffer PreloadedSubtitles { get; internal set; }

        /// <summary>
        /// Gets the worker collection
        /// </summary>
        internal MediaWorkerSet Workers { get; set; }

        /// <summary>
        /// Holds the block renderers
        /// </summary>
        internal MediaTypeDictionary<IMediaRenderer> Renderers { get; } = new MediaTypeDictionary<IMediaRenderer>();

        /// <summary>
        /// Holds the last rendered StartTime for each of the media block types
        /// </summary>
        internal MediaTypeDictionary<TimeSpan> LastRenderTime { get; } = new MediaTypeDictionary<TimeSpan>();

        /// <summary>
        /// Gets or sets a value indicating whether the decoder worker is sync-buffering.
        /// Sync-buffering is entered when there are no main blocks for the current clock.
        /// This in turn pauses the clock (without changing the media state).
        /// The decoder exits this condition when buffering is no longer needed and
        /// updates the clock position to what is available in the main block buffer.
        /// </summary>
        internal bool IsSyncBuffering
        {
            get => m_IsSyncBuffering.Value;
            set => m_IsSyncBuffering.Value = value;
        }

        /// <summary>
        /// Gets or sets a value indicating whether the decoder worker has decoded all frames.
        /// This is an indication that the rendering worker should probe for end of media scenarios
        /// </summary>
        internal bool HasDecodingEnded
        {
            get => m_HasDecodingEnded.Value;
            set => m_HasDecodingEnded.Value = value;
        }

        /// <summary>
        /// Gets the buffer length maximum.
        /// port of MAX_QUEUE_SIZE (ffplay.c)
        /// </summary>
        internal long BufferLengthMax => 16 * 1024 * 1024;

        /// <summary>
        /// Gets a value indicating whether packets can be read and
        /// room is available in the download cache.
        /// </summary>
        internal bool ShouldReadMorePackets
        {
            get
            {
                if (Container?.Components == null)
                    return false;

                if (Container.IsReadAborted || Container.IsAtEndOfStream)
                    return false;

                // If it's a live stream always continue reading, regardless
                if (Container.IsLiveStream)
                    return true;

                // For network streams always expect a minimum buffer length
                if (Container.IsNetworkStream && Container.Components.BufferLength < BufferLengthMax)
                    return true;

                // if we don't have enough packets queued we should read
                return Container.Components.HasEnoughPackets == false;
            }
        }

        #endregion

        #region Methods

        /// <summary>
        /// Gets the component start offset. Pass none to get the media start offset.
        /// </summary>
        /// <param name="t">The component media type.</param>
        /// <returns>The component start time</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TimeSpan GetComponentStartOffset(MediaType t)
        {
            var offset = t == MediaType.None
                ? Container?.Components?.PlaybackStartTime ?? TimeSpan.MinValue
                : Container.Components[t]?.StartTime ?? TimeSpan.MinValue;

            return offset == TimeSpan.MinValue ? TimeSpan.Zero : offset;
        }

        /// <summary>
        /// Converts from wall clock position to the corresponding component-equivalent playback time.
        /// Pass None to get the general component time
        /// </summary>
        /// <param name="t">The media type.</param>
        /// <param name="clockTime">The wall clock.</param>
        /// <returns>The wall clock timestamp that maps to a corresponding component time</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TimeSpan ConvertClockToPlaybackTime(MediaType t, TimeSpan clockTime) =>
            TimeSpan.FromTicks(clockTime.Ticks + GetComponentStartOffset(t).Ticks);

        /// <summary>
        /// Converts from wall clock position to the corresponding component-equivalent playback time.
        /// Pass None to get the general component time
        /// </summary>
        /// <param name="clockTime">The wall clock.</param>
        /// <returns>The wall clock timestamp that maps to a corresponding component time</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TimeSpan ConvertClockToPlaybackTime(TimeSpan clockTime) =>
            ConvertClockToPlaybackTime(MediaType.None, clockTime);

        /// <summary>
        /// Converts from wall clock position to the corresponding component-equivalent playback time.
        /// Pass None to get the general component time
        /// </summary>
        /// <returns>The wall clock timestamp that maps to a corresponding component time</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TimeSpan ConvertClockToPlaybackTime() =>
            ConvertClockToPlaybackTime(MediaType.None, WallClock);

        /// <summary>
        /// Converts from playback time to wall clock time.
        /// </summary>
        /// <param name="t">The component media type.</param>
        /// <param name="playbackTime">The playback time.</param>
        /// <returns>The wall clock time</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TimeSpan ConvertPlaybackToClockTime(MediaType t, TimeSpan playbackTime) =>
            TimeSpan.FromTicks(playbackTime.Ticks - GetComponentStartOffset(t).Ticks);

        /// <summary>
        /// Converts from playback time to wall clock time.
        /// </summary>
        /// <param name="playbackTime">The playback time.</param>
        /// <returns>The wall clock time</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TimeSpan ConvertPlaybackToClockTime(TimeSpan playbackTime) =>
            ConvertPlaybackToClockTime(MediaType.None, playbackTime);

        /// <summary>
        /// Converts from playback time to wall clock time.
        /// </summary>
        /// <returns>The wall clock time</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TimeSpan ConvertPlaybackToClockTime() =>
            ConvertPlaybackToClockTime(MediaType.None, ConvertClockToPlaybackTime());

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TimeSpan ConvertPlaybackToComponentClock(MediaType t, TimeSpan playbackClock)
        {
            var mainOffset = GetComponentStartOffset(MediaType.None);
            var componentOffset = GetComponentStartOffset(t);
            return TimeSpan.FromTicks(playbackClock.Ticks + (componentOffset.Ticks - mainOffset.Ticks));
        }

        internal TimeSpan ConvertWallClockToComponentClock(MediaType t, TimeSpan wallClock) =>
            ConvertPlaybackToComponentClock(t, ConvertClockToPlaybackTime(wallClock));

        /// <summary>
        /// Resumes the playback by resuming the clock and updating the playback state to state.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ResumePlayback()
        {
            if (!IsSyncBuffering)
                Clock.Play();

            State.UpdateMediaState(PlaybackStatus.Play);
        }

        /// <summary>
        /// Updates the clock position and notifies the new
        /// position to the <see cref="State" />.
        /// </summary>
        /// <param name="playbackPosition">The position.</param>
        /// <returns>The newly set position</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TimeSpan ChangePosition(TimeSpan playbackPosition)
        {
            // TODO -- we need to fix all occurrences of this. This is the absolute time to compute elapsed time
            Clock.Update(ConvertPlaybackToClockTime(playbackPosition));
            State.ReportPlaybackPosition();
            return playbackPosition;
        }

        /// <summary>
        /// Resets the clock to the zero position and notifies the new
        /// position to rhe <see cref="State"/>.
        /// </summary>
        /// <returns>The newly set position</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal TimeSpan ResetPosition()
        {
            Clock.Reset();
            State.ReportPlaybackPosition();
            return TimeSpan.Zero;
        }

        /// <summary>
        /// Invalidates the last render time for the given component.
        /// Additionally, it calls Seek on the renderer to remove any caches
        /// </summary>
        /// <param name="t">The t.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void InvalidateRenderer(MediaType t)
        {
            // This forces the rendering worker to send the
            // corresponding block to its renderer
            LastRenderTime[t] = TimeSpan.MinValue;
            Renderers[t]?.Seek();
        }

        /// <summary>
        /// Invalidates the last render time for all renderers given component.
        /// Additionally, it calls Seek on the renderers to remove any caches
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void InvalidateRenderers()
        {
            var mediaTypes = Renderers.Keys.ToArray();
            foreach (var t in mediaTypes)
                InvalidateRenderer(t);
        }

        #endregion
    }
}
