using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace V380Decoder.src
{
    public class RtspServer
    {
        private readonly int port;
        private readonly bool transcodeToH264;      // --rtsp-codec h264 : old behaviour (H.265 -> H.264 via ffmpeg)
        private readonly H264Transcoder transcoder; // null in native mode
        private TcpListener listener;
        private Thread acceptThread;
        private volatile bool running;

        private readonly ConcurrentDictionary<int, RtspSession> sessions = new();
        private int nextId;

        // Parameter sets of the codec actually sent to RTSP clients.
        private VideoCodec streamCodec = VideoCodec.Unknown;
        private byte[] cachedVps;
        private byte[] cachedSps;
        private byte[] cachedPps;
        private readonly object sdpLock = new();
        private readonly ManualResetEventSlim sdpReady = new(false);

        public VideoCodec StreamCodec { get { lock (sdpLock) return streamCodec; } }

        public RtspServer(int port, bool transcodeToH264 = false)
        {
            this.port = port;
            this.transcodeToH264 = transcodeToH264;
            if (transcodeToH264)
                transcoder = new H264Transcoder(PushVideoDirect);
            Console.Error.WriteLine($"[RTSP] video mode: {(transcodeToH264 ? "transcode H.265 -> H.264 (ffmpeg)" : "native passthrough (H.264 or H.265 as sent by the camera)")}");
        }

        public void Start()
        {
            listener = new TcpListener(IPAddress.Any, port);
            listener.Start(10);
            running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "rtsp-accept" };
            acceptThread.Start();
            Console.Error.WriteLine($"[RTSP] rtsp://{NetworkHelper.GetLocalIPAddress()}:{port}/live");
        }

        void AcceptLoop()
        {
            while (running)
            {
                try
                {
                    var tcp = listener.AcceptTcpClient();
                    tcp.NoDelay = true;
                    int id = Interlocked.Increment(ref nextId);
                    var s = new RtspSession(id, tcp, this);
                    sessions[id] = s;
                    s.Start();
                    s.OnClose += () => sessions.TryRemove(id, out _);
                }
                catch { }
            }
        }

        public void PushVideo(FrameData f)
        {
            if (f.Codec == VideoCodec.H265 && transcoder != null && transcoder.IsAvailable)
            {
                transcoder.PushFrame(f);
                return;
            }

            PushVideoDirect(f);
        }

        private void PushVideoDirect(FrameData f)
        {
            CacheParameterSets(f);
            foreach (var s in sessions.Values) s.PushVideo(f);
        }

        public void PushAudio(FrameData f)
        {
            foreach (var s in sessions.Values) s.PushAudio(f);
        }

        void CacheParameterSets(FrameData f)
        {
            VideoCodec codec = f.Codec == VideoCodec.Unknown ? VideoCodec.H264 : f.Codec;
            lock (sdpLock)
            {
                if (streamCodec != codec)
                {
                    if (streamCodec != VideoCodec.Unknown)
                        Console.Error.WriteLine($"[RTSP] stream codec changed {streamCodec} -> {codec}");
                    streamCodec = codec;
                    cachedVps = cachedSps = cachedPps = null;
                    sdpReady.Reset();
                }

                ParseNals(f.Payload, codec, (nalType, nal) =>
                {
                    if (codec == VideoCodec.H265)
                    {
                        if (nalType == 32) cachedVps = nal;
                        else if (nalType == 33) cachedSps = nal;
                        else if (nalType == 34) cachedPps = nal;
                    }
                    else
                    {
                        if (nalType == 7) cachedSps = nal;
                        else if (nalType == 8) cachedPps = nal;
                    }
                });

                bool complete = cachedSps != null && cachedPps != null && (codec != VideoCodec.H265 || cachedVps != null);
                if (complete && !sdpReady.IsSet)
                {
                    sdpReady.Set();
                    Console.Error.WriteLine($"[RTSP] parameter sets ready, codec={codec}");
                }
            }
        }

        internal static void ParseNals(byte[] data, VideoCodec codec, Action<int, byte[]> cb)
        {
            int i = 0;
            int len = data.Length;
            while (i < len)
            {
                int sc = FindStartCode(data, i);
                if (sc < 0) break;

                int scLen = (sc + 3 < len && data[sc + 2] == 1) ? 3 : 4;
                int nalStart = sc + scLen;
                if (nalStart >= len) break;

                int next = FindStartCode(data, nalStart);
                int nalEnd = next < 0 ? len : next;
                int nalType = codec == VideoCodec.H265
                    ? (data[nalStart] >> 1) & 0x3F
                    : data[nalStart] & 0x1F;

                var nal = new byte[nalEnd - nalStart];
                Array.Copy(data, nalStart, nal, 0, nal.Length);
                cb(nalType, nal);
                i = nalEnd;
            }
        }

        static int FindStartCode(byte[] d, int from)
        {
            for (int i = from; i + 3 < d.Length; i++)
            {
                if (d[i] == 0 && d[i + 1] == 0)
                {
                    if (d[i + 2] == 1) return i;
                    if (d[i + 2] == 0 && i + 3 < d.Length && d[i + 3] == 1) return i;
                }
            }
            return -1;
        }

        public string BuildSdp()
        {
            // A client may connect before the first keyframe: wait (bounded) for the
            // parameter sets so the SDP announces the right codec and sprop-*.
            if (!sdpReady.IsSet) sdpReady.Wait(TimeSpan.FromSeconds(10));

            string rtpmap, fmtp = "";
            lock (sdpLock)
            {
                if (streamCodec == VideoCodec.H265)
                {
                    rtpmap = "a=rtpmap:96 H265/90000\r\n";
                    if (cachedVps != null && cachedSps != null && cachedPps != null)
                        fmtp = $"a=fmtp:96 sprop-vps={Convert.ToBase64String(cachedVps)};" +
                               $"sprop-sps={Convert.ToBase64String(cachedSps)};" +
                               $"sprop-pps={Convert.ToBase64String(cachedPps)}\r\n";
                }
                else
                {
                    rtpmap = "a=rtpmap:96 H264/90000\r\n";
                    if (cachedSps != null && cachedPps != null)
                    {
                        string pli = cachedSps.Length >= 4
                            ? $"{cachedSps[1]:X2}{cachedSps[2]:X2}{cachedSps[3]:X2}"
                            : "64001F";
                        fmtp = $"a=fmtp:96 packetization-mode=1;sprop-parameter-sets={Convert.ToBase64String(cachedSps)},{Convert.ToBase64String(cachedPps)};profile-level-id={pli}\r\n";
                    }
                }
                LogUtils.debug($"[RTSP] SDP codec={streamCodec} vps={cachedVps != null} sps={cachedSps != null} pps={cachedPps != null}");
            }

            return
                "v=0\r\n" +
                "o=- 1 1 IN IP4 0.0.0.0\r\n" +
                "s=V380 Live\r\n" +
                "t=0 0\r\n" +
                "a=recvonly\r\n" +
                "m=video 0 RTP/AVP 96\r\n" +
                rtpmap +
                fmtp +
                "a=control:trackID=0\r\n" +
                "m=audio 0 RTP/AVP 8\r\n" +
                "a=rtpmap:8 PCMA/8000/1\r\n" +
                "a=control:trackID=1\r\n";
        }

        public void Dispose()
        {
            running = false;
            try { listener?.Stop(); } catch { }
            foreach (var s in sessions.Values) s.Close();
            transcoder?.Dispose();
        }
    }
}
