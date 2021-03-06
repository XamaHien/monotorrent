//
// StandardPicker.cs
//
// Authors:
//   Alan McGovern alan.mcgovern@gmail.com
//
// Copyright (C) 2006 Alan McGovern
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

using MonoTorrent.Logging;

namespace MonoTorrent.Client.PiecePicking
{
    public class StandardPicker : PiecePicker
    {
        static readonly Logger logger = Logger.Create ();

        readonly SortList<Piece> requests;

        ITorrentData TorrentData { get; set; }

        public StandardPicker ()
            : base (null)
        {
            requests = new SortList<Piece> ();
        }

        public override void CancelRequest (IPieceRequester peer, int piece, int startOffset, int length)
        {
            CancelWhere (b => b.StartOffset == startOffset &&
                              b.RequestLength == length &&
                              b.PieceIndex == piece &&
                              peer == b.RequestedOff);
        }

        public override void CancelRequests (IPieceRequester peer)
        {
            CancelWhere (b => peer == b.RequestedOff);
        }

        void CancelWhere (Predicate<Block> predicate)
        {
            bool cancelled = false;
            for (int p = 0; p < requests.Count; p++) {
                Block[] blocks = requests[p].Blocks;
                for (int i = 0; i < blocks.Length; i++) {
                    if (predicate (blocks[i]) && !blocks[i].Received) {
                        requests[p].Abandoned = true;
                        cancelled = true;
                        blocks[i].CancelRequest ();
                    }
                }
            }

            if (cancelled)
                requests.RemoveAll (p => p.NoBlocksRequested);
        }

        public override int CurrentReceivedCount ()
        {
            int count = 0;
            for (int i = 0; i < requests.Count; i++)
                count += requests[i].TotalReceived;
            return count;
        }

        public override int CurrentRequestCount ()
        {
            int count = 0;
            for (int i = 0; i < requests.Count; i++)
                count += requests[i].TotalRequested - requests[i].TotalReceived;
            return count;
        }

        public override List<Piece> ExportActiveRequests ()
        {
            return new List<Piece> (requests);
        }

        public override void Initialise (BitField bitfield, ITorrentData torrentData, IEnumerable<Piece> requests)
        {
            TorrentData = torrentData;
            this.requests.Clear ();
            foreach (Piece p in requests)
                this.requests.Add (p);
        }

        public override bool IsInteresting (BitField bitfield)
        {
            return !bitfield.AllFalse;
        }

        public override IList<PieceRequest> PickPiece (IPieceRequester peer, BitField available, IReadOnlyList<IPieceRequester> otherPeers, int count, int startIndex, int endIndex)
        {
            PieceRequest message;
            IList<PieceRequest> bundle;

            // If there is already a request on this peer, try to request the next block. If the peer is choking us, then the only
            // requests that could be continued would be existing "Fast" pieces.
            if ((message = ContinueExistingRequest (peer, startIndex, endIndex, false, false)) != null)
                return new[] { message };

            // Then we check if there are any allowed "Fast" pieces to download
            if (peer.IsChoking && (message = GetFromList (peer, available, peer.IsAllowedFastPieces)) != null)
                return new[] { message };

            // If the peer is choking, then we can't download from them as they had no "fast" pieces for us to download
            if (peer.IsChoking)
                return null;

            if ((message = ContinueExistingRequest (peer, startIndex, endIndex, true, false)) != null)
                return new[] { message };

            // We see if the peer has suggested any pieces we should request
            if ((message = GetFromList (peer, available, peer.SuggestedPieces)) != null)
                return new[] { message };

            // Now we see what pieces the peer has that we don't have and try and request one
            if ((bundle = GetStandardRequest (peer, available, startIndex, endIndex, count)) != null)
                return bundle;

            return null;
        }

        public override void Reset ()
        {
            requests.Clear ();
        }

        static readonly Func<Piece, int, int> IndexComparer = (Piece piece, int comparand)
            => piece.Index.CompareTo (comparand);

        public override bool ValidatePiece (IPieceRequester peer, int pieceIndex, int startOffset, int length, out Piece piece)
        {
            int pIndex = requests.BinarySearch (IndexComparer, pieceIndex);

            if (pIndex < 0) {
                piece = null;
                logger.InfoFormatted ("Piece validation failed: {0}-{1}. {2} No piece.", pieceIndex, startOffset, peer.Uri);
                return false;
            }
            piece = requests[pIndex];
            // Pick out the block that this piece message belongs to
            int blockIndex = Block.IndexOf (piece.Blocks, startOffset, length);
            if (blockIndex == -1 || !peer.Equals (piece.Blocks[blockIndex].RequestedOff)) {
                logger.InfoFormatted ("Piece validation failed: {0}-{1}. {2} No block ", pieceIndex, startOffset, peer.Uri);
                return false;
            }
            if (piece.Blocks[blockIndex].Received) {
                logger.InfoFormatted ("Piece validation failed: {0}-{1}. {2} Already received.", pieceIndex, startOffset, peer.Uri);
                return false;
            }
            if (!piece.Blocks[blockIndex].Requested) {
                logger.InfoFormatted ("Piece validation failed: {0}-{1}. {2} Not requested.", pieceIndex, startOffset, peer.Uri);
                return false;
            }
            peer.AmRequestingPiecesCount--;
            piece.Blocks[blockIndex].Received = true;

            if (piece.AllBlocksReceived)
                requests.RemoveAt (pIndex);
            return true;
        }

        public override PieceRequest ContinueExistingRequest (IPieceRequester peer, int startIndex, int endIndex)
            => ContinueExistingRequest (peer, startIndex, endIndex, false, false);

        PieceRequest ContinueExistingRequest (IPieceRequester peer, int startIndex, int endIndex, bool allowAbandoned, bool allowAny)
        {
            for (int req = 0; req < requests.Count; req++) {
                Piece p = requests[req];
                if (p.Index < startIndex || p.Index > endIndex || !peer.BitField[p.Index])
                    continue;

                // For each piece that was assigned to this peer, try to request a block from it
                // A piece is 'assigned' to a peer if he is the first person to request a block from that piece
                if (allowAny || (allowAbandoned && p.Abandoned && peer.RepeatedHashFails == 0) || (peer == p.Blocks[0].RequestedOff && !p.AllBlocksRequested)) {
                    for (int i = 0; i < p.BlockCount; i++) {
                        if (!p.Blocks[i].Received && !p.Blocks[i].Requested)
                            return p.Blocks[i].CreateRequest (peer);
                    }
                }
            }

            // If we get here it means all the blocks in the pieces being downloaded by the peer are already requested
            return null;
        }

        public override PieceRequest ContinueAnyExisting (IPieceRequester peer, int startIndex, int endIndex)
        {
            // If this peer is currently a 'dodgy' peer, then don't allow him to help with someone else's
            // piece request.
            if (peer.RepeatedHashFails != 0)
                return null;

            return ContinueExistingRequest (peer, startIndex, endIndex, true, true);
        }

        protected PieceRequest GetFromList (IPieceRequester peer, BitField bitfield, IList<int> pieces)
        {
            if (!peer.SupportsFastPeer || !ClientEngine.SupportsFastPeer)
                return null;

            for (int i = 0; i < pieces.Count; i++) {
                int index = pieces[i];
                // A peer should only suggest a piece he has, but just in case.
                if (index >= bitfield.Length || !bitfield[index] || AlreadyRequested (index))
                    continue;

                pieces.RemoveAt (i);
                var p = new Piece (index, TorrentData.PieceLength, TorrentData.Size);
                requests.Add (p);
                return p.Blocks[0].CreateRequest (peer);
            }


            return null;
        }

        public IList<PieceRequest> GetStandardRequest (IPieceRequester peer, BitField current, int startIndex, int endIndex, int count)
        {
            int piecesNeeded = (count * Piece.BlockSize) / TorrentData.PieceLength;
            if ((count * Piece.BlockSize) % TorrentData.PieceLength != 0)
                piecesNeeded++;
            int checkIndex = CanRequest (current, startIndex, endIndex, ref piecesNeeded);

            // Nothing to request.
            if (checkIndex == -1)
                return null;

            var bundle = new List<PieceRequest> (count);
            for (int i = 0; bundle.Count < count && i < piecesNeeded; i++) {
                // Request the piece
                var p = new Piece (checkIndex + i, TorrentData.PieceLength, TorrentData.Size);
                requests.Add (p);
                for (int j = 0; j < p.Blocks.Length && bundle.Count < count; j++)
                    bundle.Add (p.Blocks[j].CreateRequest (peer));
            }
            return bundle;
        }

        protected bool AlreadyRequested (int index)
        {
            return requests.BinarySearch (IndexComparer, index) >= 0;
        }

        int CanRequest (BitField bitfield, int pieceStartIndex, int pieceEndIndex, ref int pieceCount)
        {
            int largestStart = 0;
            int largestEnd = 0;
            while ((pieceStartIndex = bitfield.FirstTrue (pieceStartIndex, pieceEndIndex)) != -1) {
                int end = bitfield.FirstFalse (pieceStartIndex, pieceEndIndex);
                if (end == -1)
                    end = Math.Min (pieceStartIndex + pieceCount, bitfield.Length);

                for (int i = pieceStartIndex; i < end; i++)
                    if (AlreadyRequested (i))
                        end = i;

                if ((end - pieceStartIndex) >= pieceCount)
                    return pieceStartIndex;

                if ((largestEnd - largestStart) < (end - pieceStartIndex)) {
                    largestStart = pieceStartIndex;
                    largestEnd = end;
                }

                pieceStartIndex = Math.Max (pieceStartIndex + 1, end);
            }

            pieceCount = largestEnd - largestStart;
            return pieceCount == 0 ? -1 : largestStart;
        }
    }
}
