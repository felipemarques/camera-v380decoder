namespace V380Decoder.src
{
    public enum VideoCodec
    {
        Unknown,
        H264,
        H265
    }

    public class FrameData
    {
        public byte RawType;   // fragment header type byte (0x00/0x01/0x1A)
        public uint FrameId;
        public ushort FrameType;
        public ushort FrameRate;
        public ulong Timestamp;
        public VideoCodec Codec;
        public byte[] Payload;
        public bool IsKeyframe
        {
            get
            {
                if (Payload == null || Payload.Length < 5) return RawType == 0x00;

                int offset = Payload[2] == 1 ? 3 : 4;
                if (Payload.Length <= offset) return RawType == 0x00;

                byte nalHeader = Payload[offset];
                return Codec switch
                {
                    VideoCodec.H264 => (nalHeader & 0x1F) == 5 || RawType == 0x00,
                    VideoCodec.H265 => ((nalHeader >> 1) & 0x3F) is 19 or 20,
                    _ => RawType == 0x00
                };
            }
        }
    }
}
