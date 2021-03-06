﻿//
// StreamingPiecePicker.cs
//
// Authors:
//   Alan McGovern      alan.mcgovern@gmail.com
//
// Copyright (C) 2020 Alan McGovern
//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System;
using System.Collections.Generic;
using System.Linq;

namespace MonoTorrent.Client.PiecePicking
{
    /// <summary>
    /// This implementation of PiecePicker downloads pieces in a linear fashion until
    /// sufficient data has been buffered, then it moves to a standard 'rarest first'
    /// mode.
    /// </summary>
    class StreamingPiecePicker : PiecePicker
    {
        PiecePicker LowPriorityPicker { get; }

        /// <summary>
        /// This is the piece index of the block of data currently being consumed by the
        /// media player or other program.
        /// </summary>
        public int HighPriorityPieceIndex { get; private set; }

        /// <summary>
        /// The number of pieces which will be kept buffered to avoid stuttering while streaming media.
        /// </summary>
        internal int HighPriorityCount { get; set; } = 15;

        internal int LowPriorityCount => HighPriorityCount * 2;

        ITorrentData TorrentData { get; set; }


        /// <summary>
        /// Empty constructor for changing piece pickers
        /// </summary>
        public StreamingPiecePicker (PiecePicker picker)
            : base (new PriorityPicker (picker))
        {
            LowPriorityPicker = new PriorityPicker (new RarestFirstPicker (new RandomisedPicker (picker)));
        }

        public override void Initialise (BitField bitfield, ITorrentData torrentData, IEnumerable<Piece> requests)
        {
            TorrentData = torrentData;
            LowPriorityPicker.Initialise (bitfield, torrentData, Enumerable.Empty<Piece> ());
            base.Initialise (bitfield, torrentData, requests);
        }

        public override IList<PieceRequest> PickPiece (IPieceRequester peer, BitField available, IReadOnlyList<IPieceRequester> otherPeers, int count, int startIndex, int endIndex)
        {
            PieceRequest request;
            IList<PieceRequest> bundle;

            if (HighPriorityPieceIndex >= startIndex && HighPriorityPieceIndex <= endIndex) {
                var start = HighPriorityPieceIndex;
                var end = Math.Min (endIndex, HighPriorityPieceIndex + HighPriorityCount - 1);
                if ((request = BasePicker.ContinueAnyExisting (peer, start, end)) != null)
                    return new[] { request };

                if ((bundle = base.PickPiece (peer, available, otherPeers, count, start, end)) != null)
                    return bundle;
            }

            if (endIndex < HighPriorityPieceIndex)
                return null;

            var lowPriorityEndIndex = Math.Min (HighPriorityPieceIndex + LowPriorityCount, endIndex);
            if ((bundle = LowPriorityPicker.PickPiece (peer, available, otherPeers, count, HighPriorityPieceIndex, lowPriorityEndIndex)) != null)
                return bundle;

            return LowPriorityPicker.PickPiece (peer, available, otherPeers, count, HighPriorityPieceIndex, endIndex);
        }

        /// <summary>
        /// Cancel any pending requests and then issue new requests so we immediately download pieces from the new high
        /// priority set.
        /// </summary>
        /// <param name="file"></param>
        /// <param name="position"></param>
        internal bool SeekToPosition (ITorrentFileInfo file, long position)
        {
            // Update the high priority set, then cancel pending requests.
            var oldIndex = HighPriorityPieceIndex;
            ReadToPosition (file, position);
            return oldIndex != HighPriorityPieceIndex;
        }

        /// <summary>
        /// Inform the picker that we have sequentially read data and so will need to update the high priority set without
        /// cancelling pending requests.
        /// </summary>
        /// <param name="file"></param>
        /// <param name="position"></param>
        internal void ReadToPosition (ITorrentFileInfo file, long position)
        {
            HighPriorityPieceIndex = file.StartPieceIndex + (int) ((file.StartPieceOffset + position) / TorrentData.PieceLength);
        }
    }
}
